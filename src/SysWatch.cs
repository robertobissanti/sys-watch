// SysWatch - quiet machine monitor with a status LED for YASB and non-intrusive alerts.
//
//   SysWatch.exe                run the background monitor (single instance)
//   SysWatch.exe --widget       print one JSON line for the YASB "custom" widget
//                               (also starts the monitor if it is not running)
//   SysWatch.exe --toggle-snooze   mute alerts for 60 minutes / unmute
//   SysWatch.exe --status       human-readable status in the terminal
//   SysWatch.exe --test-alert   show a test notification
//   SysWatch.exe --stop         stop the monitor
//
// Copyright (c) 2026 Roberto Bissanti - BSD 3-Clause License
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "";
        Paths.Init();
        T.Init();
        switch (cmd)
        {
            case "--widget": return Widget.Run();
            case "--toggle-snooze": Snooze.Toggle(); return 0;
            case "--test-alert":
                Toast.Show(T.S("Test alert", "Avviso di prova"),
                           T.S("SysWatch notifications work.", "Le notifiche di SysWatch funzionano."), false);
                return 0;
            case "--stop":
                Daemon.RequestStop(); return 0;
            case "--status":
                AttachConsole(-1);
                Console.WriteLine(Widget.StatusText());
                return 0;
            case "--autostart":
                Autostart.Set(args.Length > 1 && args[1] == "on"); return 0;
            default:
                return Daemon.Run();
        }
    }
}

// ---------------------------------------------------------------- paths / json
static class Paths
{
    public static string Dir, StatusFile, ConfigFile, SnoozeFile, StopFile;

    public static void Init()
    {
        Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysWatch");
        Directory.CreateDirectory(Dir);
        StatusFile = Path.Combine(Dir, "status.json");
        ConfigFile = Path.Combine(Dir, "config.json");
        SnoozeFile = Path.Combine(Dir, "snooze.txt");
        StopFile = Path.Combine(Dir, "stop.flag");
    }
}

static class Json
{
    static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();
    public static string Write(object o) { return Ser.Serialize(o); }
    public static Dictionary<string, object> Read(string path)
    {
        try { return Ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path)); }
        catch { return null; }
    }
    public static double Num(Dictionary<string, object> d, string k, double def)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
        try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return def; }
    }
    public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v)) return null;
        return v as Dictionary<string, object>;
    }
}

// ---------------------------------------------------------------- language (en default, it)
static class T
{
    static bool it;
    public static void Init() { it = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it"; }
    public static string S(string en, string itl) { return it ? itl : en; }
}

// ---------------------------------------------------------------- configuration
class Rule
{
    public string Key;
    public double Warn, Crit, WarnSec, CritSec, Hyst;
    public bool Lower;     // true = a LOW value is bad (disk free, battery)
    public bool Ge(double v, double thr) { return Lower ? v <= thr : v >= thr; }
    public bool GeHyst(double v, double thr) { return Lower ? v <= thr + Hyst : v >= thr - Hyst; }
}

class Config
{
    public int IntervalSec = 5;
    public double CooldownWarnMin = 30, CooldownCritMin = 10, MinGapSec = 120, SnoozeMin = 60;
    public bool Toasts = true;
    public Rule Ram, Cpu, Temp, Disk, Battery;

    public static Config Load()
    {
        var c = new Config();
        c.Ram     = new Rule { Key = "ram",     Warn = 85, Crit = 93, WarnSec = 60,  CritSec = 20,  Hyst = 4 };
        c.Cpu     = new Rule { Key = "cpu",     Warn = 90, Crit = 98, WarnSec = 180, CritSec = 300, Hyst = 8 };
        c.Temp    = new Rule { Key = "temp",    Warn = 85, Crit = 95, WarnSec = 60,  CritSec = 15,  Hyst = 5 };
        c.Disk    = new Rule { Key = "disk",    Warn = 15, Crit = 8,  WarnSec = 0,   CritSec = 0,   Hyst = 2, Lower = true };
        c.Battery = new Rule { Key = "battery", Warn = 20, Crit = 10, WarnSec = 0,   CritSec = 0,   Hyst = 3, Lower = true };

        var d = File.Exists(Paths.ConfigFile) ? Json.Read(Paths.ConfigFile) : null;
        if (d == null)
        {
            Save(c);                       // write the defaults so the user can edit them
            return c;
        }
        c.IntervalSec = (int)Math.Max(2, Json.Num(d, "intervalSec", c.IntervalSec));
        c.CooldownWarnMin = Json.Num(d, "cooldownWarnMin", c.CooldownWarnMin);
        c.CooldownCritMin = Json.Num(d, "cooldownCritMin", c.CooldownCritMin);
        c.MinGapSec = Json.Num(d, "minGapSec", c.MinGapSec);
        c.SnoozeMin = Json.Num(d, "snoozeMin", c.SnoozeMin);
        c.Toasts = Json.Num(d, "toasts", 1) != 0;
        foreach (var r in new[] { c.Ram, c.Cpu, c.Temp, c.Disk, c.Battery })
        {
            var o = Json.Obj(d, r.Key);
            if (o == null) continue;
            r.Warn = Json.Num(o, "warn", r.Warn); r.Crit = Json.Num(o, "crit", r.Crit);
            r.WarnSec = Json.Num(o, "warnSec", r.WarnSec); r.CritSec = Json.Num(o, "critSec", r.CritSec);
        }
        return c;
    }

    static void Save(Config c)
    {
        Func<Rule, object> r = x => new Dictionary<string, object> {
            { "warn", x.Warn }, { "crit", x.Crit }, { "warnSec", x.WarnSec }, { "critSec", x.CritSec } };
        var d = new Dictionary<string, object> {
            { "_help", "ram/cpu/temp: alert when value >= warn/crit for warnSec/critSec seconds. disk = % free, battery = % (only while discharging): alert when value <= warn/crit. toasts: 1 or 0." },
            { "intervalSec", c.IntervalSec }, { "toasts", 1 },
            { "cooldownWarnMin", c.CooldownWarnMin }, { "cooldownCritMin", c.CooldownCritMin },
            { "minGapSec", c.MinGapSec }, { "snoozeMin", c.SnoozeMin },
            { "ram", r(c.Ram) }, { "cpu", r(c.Cpu) }, { "temp", r(c.Temp) },
            { "disk", r(c.Disk) }, { "battery", r(c.Battery) } };
        try { File.WriteAllText(Paths.ConfigFile, Json.Write(d)); } catch { }
    }
}

// ---------------------------------------------------------------- alert state machine for one metric
class Metric
{
    public Rule Rule;
    public int Level;                        // 0 ok, 1 warn, 2 critical
    public double Value = double.NaN;
    DateTime? sinceW, sinceC;
    public DateTime LastToastW = DateTime.MinValue, LastToastC = DateTime.MinValue;

    public Metric(Rule r) { Rule = r; }

    /// <returns>true if the level just went up</returns>
    public bool Update(double v, DateTime now)
    {
        Value = v;
        if (double.IsNaN(v)) { Level = 0; sinceW = sinceC = null; return false; }

        bool isC = Rule.Ge(v, Rule.Crit), isW = Rule.Ge(v, Rule.Warn);
        if (isC) { if (!sinceC.HasValue) sinceC = now; } else sinceC = null;
        if (isW) { if (!sinceW.HasValue) sinceW = now; } else sinceW = null;

        int target = 0;
        if (sinceC.HasValue && (now - sinceC.Value).TotalSeconds >= Rule.CritSec) target = 2;
        else if (sinceW.HasValue && (now - sinceW.Value).TotalSeconds >= Rule.WarnSec) target = 1;

        if (target < Level)                          // lowering needs hysteresis, so it does not flap
        {
            if (Level == 2 && Rule.GeHyst(v, Rule.Crit)) target = 2;
            else if (target == 0 && Rule.GeHyst(v, Rule.Warn)) target = 1;
        }
        bool up = target > Level;
        Level = target;
        return up;
    }

    public bool CooldownOk(Config c, DateTime now)
    {
        if (Level == 2) return (now - LastToastC).TotalMinutes >= c.CooldownCritMin;
        return (now - LastToastW).TotalMinutes >= c.CooldownWarnMin;
    }
    public void MarkToast(DateTime now) { if (Level == 2) LastToastC = LastToastW = now; else LastToastW = now; }
}

// ---------------------------------------------------------------- system sampling
static class Sys
{
    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME { public uint Lo, Hi; public ulong V { get { return ((ulong)Hi << 32) | Lo; } } }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    class MEMSTATUS
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMSTATUS));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool GlobalMemoryStatusEx([In, Out] MEMSTATUS m);

    static ulong lastIdle, lastBusy;
    static bool haveCpu;

    public static double CpuPercent()
    {
        FILETIME i, k, u;
        if (!GetSystemTimes(out i, out k, out u)) return double.NaN;
        ulong idle = i.V, busy = k.V + u.V - i.V;          // kernel time includes idle time
        double r = double.NaN;
        if (haveCpu && (busy + idle) > (lastBusy + lastIdle))
            r = 100.0 * (busy - lastBusy) / (double)((busy + idle) - (lastBusy + lastIdle));
        lastIdle = idle; lastBusy = busy; haveCpu = true;
        return r;
    }

    public static void Memory(out double pct, out double usedGb, out double totalGb)
    {
        var m = new MEMSTATUS();
        GlobalMemoryStatusEx(m);
        pct = m.dwMemoryLoad;
        totalGb = m.ullTotalPhys / 1073741824.0;
        usedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
    }

    public static double TempC()
    {
        try
        {
            using (var s = new ManagementObjectSearcher("root\\cimv2",
                "SELECT HighPrecisionTemperature, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
            {
                double best = double.NaN;
                foreach (ManagementObject o in s.Get())
                {
                    double k = Convert.ToDouble(o["HighPrecisionTemperature"]) / 10.0;     // tenths of Kelvin
                    if (k < 200) k = Convert.ToDouble(o["Temperature"]);                   // fallback: whole Kelvin
                    double c = k - 273.15;
                    if (c > 0 && c < 150 && (double.IsNaN(best) || c > best)) best = c;
                    o.Dispose();
                }
                return best;
            }
        }
        catch { return double.NaN; }
    }

    public static void Disk(out double freePct, out double freeGb)
    {
        freePct = double.NaN; freeGb = double.NaN;
        try
        {
            string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
            var d = new DriveInfo(root);
            freePct = 100.0 * d.AvailableFreeSpace / d.TotalSize;
            freeGb = d.AvailableFreeSpace / 1073741824.0;
        }
        catch { }
    }

    public static void Battery(out double pct, out bool discharging, out bool present)
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        present = ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery
                  && ps.BatteryLifePercent <= 1.0f;
        pct = present ? ps.BatteryLifePercent * 100.0 : double.NaN;
        discharging = present && ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
    }
}

// ---------------------------------------------------------------- who is eating the machine
class Culprits
{
    public string TopMem = "", TopCpu = "";
    Dictionary<int, TimeSpan> prev = new Dictionary<int, TimeSpan>();
    DateTime prevT = DateTime.MinValue;

    public void Sample()
    {
        var mem = new Dictionary<string, long>();
        var cpu = new Dictionary<string, double>();
        var cur = new Dictionary<int, TimeSpan>();
        DateTime now = DateTime.UtcNow;
        double el = prevT == DateTime.MinValue ? 0 : (now - prevT).TotalSeconds;
        int cores = Environment.ProcessorCount;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string n = p.ProcessName;
                if (n == "Idle" || n == "System") continue;
                long ws = p.WorkingSet64;
                long old;
                mem[n] = mem.TryGetValue(n, out old) ? old + ws : ws;
                TimeSpan t = p.TotalProcessorTime;
                cur[p.Id] = t;
                TimeSpan before;
                if (el > 0 && prev.TryGetValue(p.Id, out before))
                {
                    double pct = 100.0 * (t - before).TotalSeconds / (el * cores);
                    double oc;
                    cpu[n] = cpu.TryGetValue(n, out oc) ? oc + pct : pct;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        prev = cur; prevT = now;
        TopMem = string.Join(", ", mem.OrderByDescending(x => x.Value).Take(3)
                 .Select(x => x.Key + " " + (x.Value / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB").ToArray());
        TopCpu = string.Join(", ", cpu.Where(x => x.Value >= 1).OrderByDescending(x => x.Value).Take(3)
                 .Select(x => x.Key + " " + x.Value.ToString("0", CultureInfo.InvariantCulture) + "%").ToArray());
    }
}

// ---------------------------------------------------------------- snooze
static class Snooze
{
    public static DateTime Until()
    {
        try
        {
            long s;
            if (File.Exists(Paths.SnoozeFile) && long.TryParse(File.ReadAllText(Paths.SnoozeFile).Trim(), out s))
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);
        }
        catch { }
        return DateTime.MinValue;
    }
    public static bool Active() { return Until() > DateTime.UtcNow; }

    public static void Toggle()
    {
        try
        {
            if (Active()) { File.Delete(Paths.SnoozeFile); return; }
            var c = Config.Load();
            long s = (long)(DateTime.UtcNow.AddMinutes(c.SnoozeMin) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            File.WriteAllText(Paths.SnoozeFile, s.ToString(CultureInfo.InvariantCulture));
        }
        catch { }
    }
}

// ---------------------------------------------------------------- Windows toast (via Windows PowerShell 5.1 + WinRT)
static class Toast
{
    const string AppId = "SysWatch";

    static void RegisterApp()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId))
            {
                k.SetValue("DisplayName", "SysWatch");
                k.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    static string Esc(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    /// <param name="loud">false = silent toast, never plays a sound</param>
    public static void Show(string title, string body, bool loud)
    {
        RegisterApp();
        string xml = "<toast><visual><binding template=\"ToastGeneric\"><text>" + Esc(title) + "</text><text>" + Esc(body)
                   + "</text></binding></visual>" + (loud ? "" : "<audio silent=\"true\"/>") + "</toast>";
        string ps =
            "$ErrorActionPreference='Stop';" +
            "[void][Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime];" +
            "[void][Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom.XmlDocument,ContentType=WindowsRuntime];" +
            "$x=New-Object Windows.Data.Xml.Dom.XmlDocument;$x.LoadXml('" + xml.Replace("'", "''") + "');" +
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('" + AppId + "').Show([Windows.UI.Notifications.ToastNotification]::new($x))";
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(ps)));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
        }
        catch { }
    }
}

// ---------------------------------------------------------------- the monitor
static class Daemon
{
    public const string MutexName = "SysWatch_Daemon";

    public static void RequestStop() { try { File.WriteAllText(Paths.StopFile, "1"); } catch { } }

    public static int Run()
    {
        bool created;
        using (var mutex = new Mutex(true, MutexName, out created))
        {
            if (!created) return 0;
            try { File.Delete(Paths.StopFile); } catch { }

            var cfg = Config.Load();
            DateTime cfgStamp = File.GetLastWriteTimeUtc(Paths.ConfigFile);
            var ram = new Metric(cfg.Ram); var cpu = new Metric(cfg.Cpu); var temp = new Metric(cfg.Temp);
            var disk = new Metric(cfg.Disk); var bat = new Metric(cfg.Battery);
            var all = new[] { ram, cpu, temp, disk, bat };
            var who = new Culprits();
            DateTime lastToast = DateTime.MinValue, lastProc = DateTime.MinValue;

            while (!File.Exists(Paths.StopFile))
            {
                DateTime now = DateTime.UtcNow;
                try
                {
                    // pick up edits of config.json without a restart
                    DateTime st = File.GetLastWriteTimeUtc(Paths.ConfigFile);
                    if (st != cfgStamp) { cfgStamp = st; cfg = Config.Load(); ram.Rule = cfg.Ram; cpu.Rule = cfg.Cpu; temp.Rule = cfg.Temp; disk.Rule = cfg.Disk; bat.Rule = cfg.Battery; }

                    double memPct, usedGb, totalGb, diskPct, diskGb, batPct; bool dis, hasBat;
                    double cpuPct = Sys.CpuPercent();
                    Sys.Memory(out memPct, out usedGb, out totalGb);
                    double tempC = Sys.TempC();
                    Sys.Disk(out diskPct, out diskGb);
                    Sys.Battery(out batPct, out dis, out hasBat);

                    if ((now - lastProc).TotalSeconds >= 10) { who.Sample(); lastProc = now; }

                    var ups = new List<Metric>();
                    if (ram.Update(memPct, now)) ups.Add(ram);
                    if (cpu.Update(cpuPct, now)) ups.Add(cpu);
                    if (temp.Update(tempC, now)) ups.Add(temp);
                    if (disk.Update(diskPct, now)) ups.Add(disk);
                    if (bat.Update(hasBat && dis ? batPct : double.NaN, now)) ups.Add(bat);

                    bool snoozed = Snooze.Active();
                    if (cfg.Toasts && !snoozed && ups.Count > 0 && (now - lastToast).TotalSeconds >= cfg.MinGapSec)
                    {
                        // one notification at a time: the most severe metric that is off cooldown
                        var m = ups.Where(x => x.CooldownOk(cfg, now)).OrderByDescending(x => x.Level).FirstOrDefault();
                        if (m != null)
                        {
                            string title, body;
                            Describe(m, who, diskGb, out title, out body);
                            Toast.Show(title, body, false);
                            m.MarkToast(now); lastToast = now;
                        }
                    }

                    int level = all.Max(x => x.Level);
                    var status = new Dictionary<string, object> {
                        { "ts", (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds },
                        { "level", level },
                        { "cpu", Round(cpuPct) }, { "ram", Round(memPct) }, { "ramUsedGb", Math.Round(usedGb, 1) }, { "ramTotalGb", Math.Round(totalGb, 1) },
                        { "temp", double.IsNaN(tempC) ? (object)null : Round(tempC) },
                        { "diskFree", double.IsNaN(diskPct) ? (object)null : Round(diskPct) }, { "diskFreeGb", double.IsNaN(diskGb) ? (object)null : Math.Round(diskGb, 0) },
                        { "battery", hasBat ? (object)Round(batPct) : null }, { "discharging", dis },
                        { "levels", new Dictionary<string, object> { { "ram", ram.Level }, { "cpu", cpu.Level }, { "temp", temp.Level }, { "disk", disk.Level }, { "battery", bat.Level } } },
                        { "topMem", who.TopMem }, { "topCpu", who.TopCpu } };
                    string tmp = Paths.StatusFile + ".tmp";
                    File.WriteAllText(tmp, Json.Write(status));
                    if (File.Exists(Paths.StatusFile)) File.Replace(tmp, Paths.StatusFile, null); else File.Move(tmp, Paths.StatusFile);
                }
                catch { }
                Thread.Sleep(cfg.IntervalSec * 1000);
            }
            try { File.Delete(Paths.StopFile); } catch { }
        }
        return 0;
    }

    static double Round(double v) { return double.IsNaN(v) ? 0 : Math.Round(v, 0); }

    static void Describe(Metric m, Culprits who, double diskGb, out string title, out string body)
    {
        bool crit = m.Level == 2;
        string pre = crit ? T.S("Critical: ", "Critico: ") : "";
        string v = m.Value.ToString("0", CultureInfo.InvariantCulture);
        switch (m.Rule.Key)
        {
            case "ram":
                title = pre + T.S("Memory is running low (" + v + "%)", "Memoria quasi esaurita (" + v + "%)");
                body = who.TopMem.Length > 0 ? T.S("Using the most: ", "Chi ne usa di più: ") + who.TopMem : T.S("Close some programs.", "Chiudi qualche programma.");
                break;
            case "cpu":
                title = pre + T.S("CPU under heavy load (" + v + "%)", "CPU molto sotto sforzo (" + v + "%)");
                body = who.TopCpu.Length > 0 ? T.S("Using the most: ", "Chi la usa di più: ") + who.TopCpu : T.S("It has been high for a while.", "È alta da un po'.");
                break;
            case "temp":
                title = pre + T.S("CPU is hot (" + v + " °C)", "CPU calda (" + v + " °C)");
                body = who.TopCpu.Length > 0 ? T.S("Using the most: ", "Chi la usa di più: ") + who.TopCpu : T.S("Check ventilation and load.", "Controlla ventilazione e carico.");
                break;
            case "disk":
                title = pre + T.S("Disk C: almost full (" + v + "% free)", "Disco C: quasi pieno (" + v + "% libero)");
                body = T.S(diskGb.ToString("0") + " GB left.", "Restano " + diskGb.ToString("0") + " GB.");
                break;
            default:
                title = pre + T.S("Battery low (" + v + "%)", "Batteria scarica (" + v + "%)");
                body = T.S("Plug in the charger.", "Collega il caricatore.");
                break;
        }
    }
}

// ---------------------------------------------------------------- autostart (optional)
static class Autostart
{
    public static void Set(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if (on) k.SetValue("SysWatch", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
            else k.DeleteValue("SysWatch", false);
        }
    }
}

// ---------------------------------------------------------------- YASB widget output
static class Widget
{
    const string Green = "#22c55e", Amber = "#f59e0b", Red = "#ef4444", Gray = "#6b7280";

    static Dictionary<string, object> Fresh()
    {
        var s = File.Exists(Paths.StatusFile) ? Json.Read(Paths.StatusFile) : null;
        if (s == null) return null;
        long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        return now - (long)Json.Num(s, "ts", 0) <= 30 ? s : null;
    }

    static void EnsureDaemon()
    {
        Mutex m;
        if (Mutex.TryOpenExisting(Daemon.MutexName, out m)) { m.Dispose(); return; }
        try
        {
            // ShellExecute: the monitor must NOT inherit our stdout pipe, otherwise whoever
            // reads this widget's output (YASB) would wait until the monitor exits
            var psi = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location);
            psi.UseShellExecute = true; psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);
        }
        catch { }
    }

    static string Num(Dictionary<string, object> s, string k) { return Json.Num(s, k, 0).ToString("0", CultureInfo.InvariantCulture); }

    public static int Run()
    {
        EnsureDaemon();
        var s = Fresh();
        bool snoozed = Snooze.Active();
        string color, dot, text, tip, alt;
        if (s == null)
        {
            color = Gray; dot = "●"; text = "…";
            tip = T.S("SysWatch is starting or not running.", "SysWatch si sta avviando o non è attivo.");
            alt = text;
        }
        else
        {
            int level = (int)Json.Num(s, "level", 0);
            color = level >= 2 ? Red : level == 1 ? Amber : Green;
            dot = snoozed ? "○" : "●";
            object t; s.TryGetValue("temp", out t);
            text = t != null ? Num(s, "temp") + "°" : Num(s, "cpu") + "%";
            alt = "CPU " + Num(s, "cpu") + "%  RAM " + Num(s, "ram") + "%" + (t != null ? "  " + Num(s, "temp") + "°" : "")
                  + "  C: " + Num(s, "diskFree") + "%";
            tip = Detail(s, snoozed);
        }
        // raw UTF-8 bytes: works even though this is a windowless (GUI-subsystem) exe
        // YASB substitutes {data[..]} only outside <span> tags, so the colored dot is pre-rendered here
        string led = "<span style='color:" + color + ";font-size:17px'>" + dot + "</span> ";
        string json = Json.Write(new Dictionary<string, object> {
            { "dot", dot }, { "color", color }, { "text", text }, { "alt", alt }, { "tip", tip },
            { "html", led + text }, { "html_alt", led + alt } }) + "\n";
        byte[] b = new UTF8Encoding(false).GetBytes(json);
        using (var o = Console.OpenStandardOutput()) { o.Write(b, 0, b.Length); o.Flush(); }
        return 0;
    }

    static string Detail(Dictionary<string, object> s, bool snoozed)
    {
        var lv = Json.Obj(s, "levels");
        var sb = new StringBuilder();
        object temp, bat; s.TryGetValue("temp", out temp); s.TryGetValue("battery", out bat);
        sb.AppendLine("CPU " + Num(s, "cpu") + "%");
        sb.AppendLine("RAM " + Num(s, "ram") + "%  (" + Json.Num(s, "ramUsedGb", 0).ToString("0.0", CultureInfo.InvariantCulture) + " / "
                      + Json.Num(s, "ramTotalGb", 0).ToString("0.0", CultureInfo.InvariantCulture) + " GB)");
        if (temp != null) sb.AppendLine(T.S("Temperature ", "Temperatura ") + Num(s, "temp") + " °C");
        sb.AppendLine(T.S("Disk C: ", "Disco C: ") + Num(s, "diskFree") + T.S("% free (", "% libero (") + Num(s, "diskFreeGb") + " GB)");
        if (bat != null) sb.AppendLine(T.S("Battery ", "Batteria ") + Num(s, "battery") + "%"
            + ((bool)s["discharging"] ? T.S(" (on battery)", " (a batteria)") : T.S(" (plugged in)", " (in carica/rete)")));
        string[] names = { "ram", "cpu", "temp", "disk", "battery" };
        string[] labels = { "RAM", "CPU", T.S("Temperature", "Temperatura"), T.S("Disk", "Disco"), T.S("Battery", "Batteria") };
        var warn = new List<string>();
        for (int i = 0; i < names.Length; i++)
        {
            int l = (int)Json.Num(lv, names[i], 0);
            if (l > 0) warn.Add(labels[i] + (l == 2 ? T.S(" (critical)", " (critico)") : T.S(" (high)", " (alto)")));
        }
        sb.AppendLine();
        sb.AppendLine(warn.Count == 0 ? T.S("All good.", "Tutto a posto.") : T.S("Attention: ", "Attenzione: ") + string.Join(", ", warn.ToArray()));
        string tm = s.ContainsKey("topMem") ? Convert.ToString(s["topMem"]) : "";
        string tc = s.ContainsKey("topCpu") ? Convert.ToString(s["topCpu"]) : "";
        if (tm.Length > 0) sb.AppendLine(T.S("Top memory: ", "Più memoria: ") + tm);
        if (tc.Length > 0) sb.AppendLine(T.S("Top CPU: ", "Più CPU: ") + tc);
        sb.Append(snoozed ? T.S("Alerts muted - click to unmute", "Avvisi silenziati - clic per riattivarli")
                          : T.S("Click: mute alerts for 1 h", "Clic: silenzia gli avvisi per 1 ora"));
        return sb.ToString();
    }

    public static string StatusText()
    {
        var s = Fresh();
        if (s == null) return T.S("SysWatch is not running (start SysWatch.exe).", "SysWatch non è in esecuzione (avvia SysWatch.exe).");
        return Detail(s, Snooze.Active());
    }
}
