// Mobius01 setup wizard (and, compiled with /define:UNINSTALLER, Desinstalar.exe).
// Spanish and English; the language chosen here is also the game's language.
//
// Resources (built by build_setup.ps1):
//   game.tar.xz       the game folder: launchers, runtime, DLLs, shaders, mods
//                     (all off) and the source for the per-PC compile.  No game
//                     data: ps2_image.bin is rebuilt from the player's own ISO,
//                     and the game always plays from that ISO.
//   toolchain.tar.xz  only what the per-PC compile needs: the UCRT64 packages
//                     gcc, cmake, ninja, pkgconf, SDL3, shaderc and Vulkan with
//                     their dependencies (no bash, pacman, Python or docs),
//                     unpacked to C:\msys64 when they are not installed.
//   AC4.Background.jpg  the art of the compiler window.
//   CREDITOS.md / CREDITS.md  the credits, in each language.
// Both archives are xz (LZMA2) and are unpacked with Windows' own tar.exe.
//
// C# 5 / .NET Framework 4.x; built by build_setup.ps1 with Theme.cs, Builder.cs
// (taskbar progress) and ..\installer\InstallerCore.cs (ISO check and memory image).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

#if UNINSTALLER
[assembly: AssemblyTitle("Mobius01 - Desinstalar / Uninstall")]
#else
[assembly: AssemblyTitle("Mobius01 - Instalar / Setup")]
#endif
[assembly: AssemblyProduct("Mobius01 (Ace Combat 04: Shattered Skies)")]

static class Product
{
    public const string Project = "Mobius01";
    public const string Game = "Ace Combat 04: Shattered Skies";
    public const string LinkName = "Ace Combat 04 Shattered Skies";   // ':' is not allowed in file names
    public const string Key = "Mobius01";
    public static string MsysDir
    {
        get
        {
            string m = Environment.GetEnvironmentVariable("AC4_MSYS");   // same override as Builder.cs
            return string.IsNullOrEmpty(m) ? @"C:\msys64" : m.TrimEnd('\\');
        }
    }
    // AC4_SETUP_TEST=1 (automatic tests): shortcuts and the uninstall entry go
    // to %TEMP%\ac4-setup-test instead of the real desktop, Start menu and list
    public static bool Test { get { return Environment.GetEnvironmentVariable("AC4_SETUP_TEST") == "1"; } }
    static string TestDir { get { return Path.Combine(Path.GetTempPath(), "ac4-setup-test"); } }
    public static string UninstallKey
    {
        get { return @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Key + (Test ? "-test" : ""); }
    }
    public static string StartMenuDir
    {
        get
        {
            return Path.Combine(Test ? Path.Combine(TestDir, "Programs") : Environment.GetFolderPath(Environment.SpecialFolder.Programs), LinkName);
        }
    }
    public static string DesktopLink
    {
        get
        {
            string desk = Test ? Path.Combine(TestDir, "Desktop") : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Directory.CreateDirectory(desk);
            return Path.Combine(desk, LinkName + ".lnk");
        }
    }
}

// --------------------------------------------------------------------- language
static class L
{
    // Spanish by default on a Spanish Windows, English everywhere else
    public static bool En = !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase);
    public static string T(string es, string en) { return En ? en : es; }
}

// ------------------------------------------------------------------ dependencies
static class Deps
{
    public static bool Windows64
    {
        get { return Environment.Is64BitOperatingSystem && Environment.OSVersion.Version.Major >= 10; }
    }

    public static string TarExe
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe"); }
    }

    public static int NetRelease
    {
        get
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    if (k != null && k.GetValue("Release") is int) return (int)k.GetValue("Release");
            }
            catch { }
            return 0;
        }
    }

    public static bool VulkanLoader
    {
        get { return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vulkan-1.dll")); }
    }

    public static List<string> Gpus()
    {
        var list = new List<string>();
        try
        {
            using (var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                if (cls != null)
                    foreach (string sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length != 4 || !char.IsDigit(sub[0])) continue;
                        try
                        {
                            using (var k = cls.OpenSubKey(sub))
                            {
                                object d = k == null ? null : k.GetValue("DriverDesc");
                                if (d != null && !list.Contains(d.ToString())) list.Add(d.ToString());
                            }
                        }
                        catch { }
                    }
        }
        catch { }
        return list.Where(g => !g.StartsWith("Microsoft Basic", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static string DriverPage(IEnumerable<string> gpus)
    {
        foreach (string g in gpus)
        {
            string u = g.ToUpperInvariant();
            if (u.Contains("NVIDIA")) return "https://www.nvidia.com/Download/index.aspx";
            if (u.Contains("AMD") || u.Contains("RADEON")) return "https://www.amd.com/support";
            if (u.Contains("INTEL")) return "https://www.intel.com/content/www/us/en/download-center/home.html";
        }
        return "https://www.vulkan.org/";
    }

    static string PackageName(string dir)
    {
        int b = dir.LastIndexOf('-');
        if (b <= 0) return dir;
        int a = dir.LastIndexOf('-', b - 1);
        return a <= 0 ? dir : dir.Substring(0, a);
    }

    // the packages Builder.cs needs, read from pacman's database
    public static List<string> MissingPackages()
    {
        var have = new HashSet<string>();
        string db = Path.Combine(Product.MsysDir, @"var\lib\pacman\local");
        if (Directory.Exists(db))
            foreach (string d in Directory.GetDirectories(db)) have.Add(PackageName(Path.GetFileName(d)));
        return new[] { "gcc", "cmake", "ninja", "pkgconf", "sdl3", "shaderc", "vulkan-headers", "vulkan-loader" }
            .Select(p => "mingw-w64-ucrt-x86_64-" + p).Where(p => !have.Contains(p)).ToList();
    }

    public static bool ToolsReady
    {
        get { return MissingPackages().Count == 0 && File.Exists(Path.Combine(Product.MsysDir, @"ucrt64\bin\cmake.exe")); }
    }

    public static long FreeBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace; }
        catch { return -1; }
    }
}

// --------------------------------------------------------------------- helpers
static class Files
{
    // files an update must not overwrite: saves, settings and the player's choices
    public static bool Preserved(string rel)
    {
        string r = rel.Replace('/', '\\').ToLowerInvariant();
        if (r.StartsWith(@"vulkanruntime\saves\") || r.StartsWith(@"vulkanruntime\logs\")) return true;
        if (r == @"vulkanruntime\cheats.pnach") return true;
        if (r.StartsWith(@"vulkanruntime\launcher-") && r.EndsWith(".txt")) return true;
        if (r.StartsWith(@"vulkanruntime\mods\") && r.EndsWith(@"\mod.toml")) return true;
        if (r == @"vulkanruntime\runtime\ac4_settings.ini") return true;
        return false;
    }

    // .lnk; runAsAdmin sets SLDF_RUNAS_USER ("Run as administrator")
    public static void CreateShortcut(string path, string target, string workDir, string description, bool runAsAdmin)
    {
        object shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            Type t = link.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workDir });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { description });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
        if (runAsAdmin)
        {
            byte[] b = File.ReadAllBytes(path);
            // ShellLinkHeader: HeaderSize 0x4C, LinkFlags (u32) at 0x14; RunAsUser = 0x2000
            if (b.Length > 0x18 && BitConverter.ToUInt32(b, 0) == 0x4C)
            {
                b[0x15] |= 0x20;
                File.WriteAllBytes(path, b);
            }
        }
    }

    // a whole folder, including paths over 260 characters
    public static void DeleteLong(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            string full = Path.GetFullPath(dir);
            string p = full.StartsWith(@"\\?\") ? full : @"\\?\" + full;
            foreach (string f in Directory.GetFiles(p, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(p, true);
        }
        catch { }
        // fallback: Windows' own rd understands \\?\ on every .NET version
        if (Directory.Exists(dir))
            try
            {
                using (var r = Process.Start(new ProcessStartInfo("cmd.exe", "/c rd /s /q \"\\\\?\\" + Path.GetFullPath(dir) + "\"")
                                             { CreateNoWindow = true, UseShellExecute = false }))
                    r.WaitForExit(60000);
            }
            catch { }
    }

    // .NET 4.6.2+ handles \\?\ and long paths when the legacy mode is off
    public static void ModernPaths()
    {
        try
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        }
        catch { }
    }

    public static long FolderSizeKb(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024; }
        catch { return 0; }
    }

    // the game's language: launcher-language.txt (read by Configurar AC4 and
    // Jugar.exe) and the Spanish translation mod switched on or off with it
    public static void SetGameLanguage(string gameDir, bool english)
    {
        string vr = Path.Combine(gameDir, "VulkanRuntime");
        File.WriteAllText(Path.Combine(vr, "launcher-language.txt"), english ? "en" : "es", new UTF8Encoding(false));
        string manifest = Path.Combine(vr, @"mods\espanol\mod.toml");
        if (!File.Exists(manifest)) return;
        var lines = File.ReadAllLines(manifest).ToList();
        int i = lines.FindIndex(l => l.TrimStart().StartsWith("enabled", StringComparison.OrdinalIgnoreCase) && l.Contains("="));
        string value = "enabled = " + (english ? "false" : "true");
        if (i >= 0) lines[i] = value; else lines.Insert(0, value);
        File.WriteAllLines(manifest, lines.ToArray(), new UTF8Encoding(false));
    }
}

#if !UNINSTALLER
// ------------------------------------------------------------------- install
class InstallJob
{
    public string Iso, Dest;
    public bool Tools, English;
    public Action<double, string, string> Progress;   // fraction, phase, detail
    public CancellationToken Cancel;
    public bool Updated;
    Process tar;

    public static bool IsInstall(string dir)
    {
        return File.Exists(Path.Combine(dir, "Jugar.exe")) && Directory.Exists(Path.Combine(dir, "VulkanRuntime"));
    }

    static Stream Resource(string name)
    {
        var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (s == null) throw new InvalidDataException(L.T("Al instalador le falta ", "The setup is missing ") + name + ".");
        return s;
    }

    // resource -> temp file -> Windows' tar.exe (xz); progress counts the entries
    void Unpack(string resource, int entries, string into, string temp, double from, double to, string phase)
    {
        string archive = Path.Combine(temp, resource);
        using (var src = Resource(resource))
        using (var dst = new FileStream(archive, FileMode.Create))
        {
            byte[] buf = new byte[1 << 20];
            long total = src.Length, done = 0;
            for (int n; (n = src.Read(buf, 0, buf.Length)) > 0; )
            {
                Cancel.ThrowIfCancellationRequested();
                dst.Write(buf, 0, n); done += n;
                Progress(from + (to - from) * 0.08 * done / total, phase, L.T("Preparando los archivos...", "Preparing the files..."));
            }
        }
        Directory.CreateDirectory(into);
        var psi = new ProcessStartInfo(Deps.TarExe, "-xvf \"" + archive + "\" -C \"" + into + "\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        int seen = 0;
        var errors = new StringBuilder();
        using (tar = Process.Start(psi))
        {
            tar.OutputDataReceived += (s, e) => { };
            tar.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.StartsWith("x "))
                {
                    seen++;
                    if ((seen & 31) == 0)
                        Progress(from + (to - from) * (0.08 + 0.92 * Math.Min(1.0, (double)seen / Math.Max(1, entries))), phase,
                                 e.Data.Substring(2));
                }
                else lock (errors) errors.AppendLine(e.Data);
            };
            tar.BeginErrorReadLine();
            tar.BeginOutputReadLine();
            while (!tar.WaitForExit(200))
                if (Cancel.IsCancellationRequested) { try { tar.Kill(); } catch { } }
            tar.WaitForExit();
            Cancel.ThrowIfCancellationRequested();
            if (tar.ExitCode != 0)
                throw new IOException(L.T("No se pudieron descomprimir los archivos", "The files could not be unpacked") +
                                      " (tar " + tar.ExitCode + "):\n" + errors.ToString().Trim());
        }
        tar = null;
        File.Delete(archive);
    }

    static void DeleteTree(string dir) { Files.DeleteLong(dir); }

    static void MoveTree(string from, string to, bool update)
    {
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = f.Substring(from.Length + 1);
            string dst = Path.Combine(to, rel);
            if (update && Files.Preserved(rel) && File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (File.Exists(dst)) { File.SetAttributes(dst, FileAttributes.Normal); File.Delete(dst); }
            File.Move(f, dst);
        }
    }

    public void Run()
    {
        Dest = Path.GetFullPath(Dest).TrimEnd('\\');
        bool update = IsInstall(Dest);
        Updated = update;
        if (!update && Directory.Exists(Dest) && Directory.EnumerateFileSystemEntries(Dest).Any())
            throw new IOException(L.T("La carpeta no está vacía y no tiene una instalación del juego.",
                                      "The folder is not empty and has no installation of the game."));
        if (!File.Exists(Deps.TarExe))
            throw new IOException(L.T("Falta ", "Missing ") + Deps.TarExe +
                                  L.T(" (incluido en Windows 10 1803 y posteriores).", " (included in Windows 10 1803 and later)."));

        Progress(0.01, L.T("Comprobando tu ISO", "Checking your ISO"), "Ace Combat 04 USA (SLUS-20152)");
        byte[] image;
        image = InstallerCore.ValidateDisc(Iso);   // an ISO or a folder with the disc's files
        Cancel.ThrowIfCancellationRequested();

        string temp = Path.Combine(Path.GetTempPath(), "mobius01-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            bool toolsNeeded = Tools && !Deps.ToolsReady;
            double gameEnd = toolsNeeded ? 0.30 : 0.92;
            // a fresh folder receives the files directly; an update unpacks
            // beside it and then replaces everything but the player's files
            string parent = Path.GetDirectoryName(Dest);
            Directory.CreateDirectory(parent);
            string unpackTo = update ? Path.Combine(parent, ".mobius01-update-" + Guid.NewGuid().ToString("N")) : Dest;
            try
            {
                Unpack("game.tar.xz", PayloadInfo.GameEntries, unpackTo, temp, 0.03, gameEnd, L.T("Instalando el juego", "Installing the game"));
                if (update)
                {
                    Progress(gameEnd, L.T("Actualizando la instalación", "Updating the installation"),
                             L.T("Se conservan tus partidas, ajustes y mods", "Your saves, settings and mods are kept"));
                    string stamp = Path.Combine(Dest, @"VulkanRuntime\runtime\ac4_build.stamp");
                    if (File.Exists(stamp)) File.Delete(stamp);   // the generic ac4.exe replaces the compiled one
                    MoveTree(unpackTo, Dest, true);
                }
            }
            finally
            {
                if (update) DeleteTree(unpackTo);
            }
            // the game's memory image, made from the player's ISO: <folder>\gamedata
            // (gzip: about 4 MB down to under 1 MB; ac4.exe reads it either way)
            Directory.CreateDirectory(Path.Combine(Dest, "gamedata"));
            using (var fs = new FileStream(Path.Combine(Dest, @"gamedata\ps2_image.bin"), FileMode.Create))
            using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal))
                gz.Write(image, 0, image.Length);
            string oldImage = Path.Combine(Dest, @"VulkanRuntime\runtime\ps2_image.bin");
            if (File.Exists(oldImage)) File.Delete(oldImage);   // where versions before 2026-11-16 put it
            Directory.CreateDirectory(Path.Combine(Dest, @"VulkanRuntime\saves"));
            Directory.CreateDirectory(Path.Combine(Dest, @"VulkanRuntime\logs"));
            // the game plays from the player's ISO or disc folder, which is
            // neither copied nor changed
            File.WriteAllText(Path.Combine(Dest, @"VulkanRuntime\launcher-iso.txt"), Path.GetFullPath(Iso), new UTF8Encoding(false));
            Files.SetGameLanguage(Dest, English);

            if (toolsNeeded)
                Unpack("toolchain.tar.xz", PayloadInfo.ToolEntries, Path.GetDirectoryName(Product.MsysDir), temp, gameEnd, 0.96,
                       L.T("Instalando las herramientas de compilación", "Installing the build tools"));
        }
        finally
        {
            DeleteTree(temp);
        }

        Progress(0.97, L.T("Creando accesos directos", "Creating shortcuts"), Product.LinkName);
        string jugar = Path.Combine(Dest, "Jugar.exe"), config = Path.Combine(Dest, "Configurar AC4.exe"),
               uninst = Path.Combine(Dest, "Desinstalar.exe");
        string play = L.T("Jugar ", "Play ") + Product.Game;
        Files.CreateShortcut(Product.DesktopLink, jugar, Dest, play, true);
        Directory.CreateDirectory(Product.StartMenuDir);
        Files.CreateShortcut(Path.Combine(Product.StartMenuDir, Product.LinkName + ".lnk"), jugar, Dest, play, true);
        Files.CreateShortcut(Path.Combine(Product.StartMenuDir, L.T("Configurar AC4.lnk", "AC4 Settings.lnk")), config, Dest,
                             L.T("Gráficos, controles, mods y cheats", "Graphics, controls, mods and cheats"), false);
        if (File.Exists(uninst))
            Files.CreateShortcut(Path.Combine(Product.StartMenuDir, L.T("Desinstalar.lnk", "Uninstall.lnk")), uninst, Dest,
                                 L.T("Desinstalar ", "Uninstall ") + Product.Game, false);

        using (var k = Registry.CurrentUser.CreateSubKey(Product.UninstallKey))
        {
            k.SetValue("DisplayName", Product.Game + " (" + Product.Project + ")");
            k.SetValue("DisplayVersion", PayloadInfo.Version);
            k.SetValue("Publisher", Product.Project);
            k.SetValue("InstallLocation", Dest);
            k.SetValue("DisplayIcon", jugar + ",0");
            k.SetValue("UninstallString", "\"" + uninst + "\"");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, Files.FolderSizeKb(Dest)), RegistryValueKind.DWord);
        }
        Progress(1, update ? L.T("Actualización terminada", "Update finished") : L.T("Instalación terminada", "Installation finished"), "");
    }

    public void Abort()
    {
        var t = tar;
        if (t != null) try { t.Kill(); } catch { }
    }
}

// ---------------------------------------------------------------------- wizard
// A borderless window like the compiler's (Builder.cs, BuildForm): the art on
// the right fading into a dark panel that holds each step.
class Wizard : Form
{
    static readonly Color Ink = Color.FromArgb(236, 242, 250), Muted = Color.FromArgb(150, 165, 185),
                          Accent = Color.FromArgb(76, 182, 255), Accent2 = Color.FromArgb(132, 232, 255),
                          Ok = Color.FromArgb(120, 220, 150), Bad = Color.FromArgb(255, 140, 110),
                          Field = Color.FromArgb(22, 30, 44);
    readonly float s;
    readonly Image bg;
    readonly Font fLabel, fTitle, fLead, fBody, fSmall, fPct, fMono, fButton, fStep, fPhase;
    int page;
    readonly List<Control>[] pages = new List<Control>[6];
    Rectangle rClose, rMin;
    bool hoverClose, hoverMin;
    Point drag;
    Button bBack, bNext, bCancel, bEs, bEn, bIso, bIsoDir, bDir;
    readonly List<Button> bCredits = new List<Button>();

    readonly TextBox tbIso = new TextBox(), tbDir = new TextBox();
    Label isoHelp, isoState, dirHelp, dirState, depList, langLabel;
    DarkCheckBox ckTools, ckPlay;
    string isoOk, isoChecked, isoError;
    bool installing, installed, failed;
    double shown, target;
    string phase = "", detail = "", error;
    InstallJob job;
    CancellationTokenSource cts;
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
    readonly Stopwatch clock = new Stopwatch();
    TaskbarProgress taskbar;

    int Px(float v) { return (int)Math.Round(v * s); }
    static string T(string es, string en) { return L.T(es, en); }

    string[] Steps
    {
        get
        {
            return L.En ? new[] { "Start", "Your game", "Folder", "Review", "Installing", "Done" }
                        : new[] { "Inicio", "Tu juego", "Carpeta", "Revisar", "Instalando", "Listo" };
        }
    }

    public Wizard()
    {
        using (var g = CreateGraphics()) s = g.DpiX / 96f;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Px(960), Px(600));
        BackColor = Color.FromArgb(8, 12, 20);
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        try { var st = GetType().Assembly.GetManifestResourceStream("AC4.Background.jpg"); if (st != null) bg = Image.FromStream(st); } catch { }
        fLabel = new Font("Segoe UI Semibold", 9f); fTitle = new Font("Segoe UI Semibold", 24f);
        fLead = new Font("Segoe UI", 11f); fBody = new Font("Segoe UI", 9.75f); fSmall = new Font("Segoe UI", 9f);
        fPct = new Font("Segoe UI Light", 34f); fMono = new Font("Consolas", 8.5f);
        fButton = new Font("Segoe UI Semibold", 9.5f); fStep = new Font("Segoe UI Semibold", 8.5f);
        fPhase = new Font("Segoe UI Semibold", 11.5f);
        rClose = new Rectangle(ClientSize.Width - Px(46), 0, Px(46), Px(36));
        rMin = new Rectangle(ClientSize.Width - Px(92), 0, Px(46), Px(36));
        for (int i = 0; i < pages.Length; i++) pages[i] = new List<Control>();

        bCancel = MakeButton("", false); bNext = MakeButton("", true); bBack = MakeButton("", false);
        bCancel.Click += delegate { Close(); };
        bNext.Click += delegate { Next(); };
        bBack.Click += delegate { if (page > 0 && !installing) GoTo(page - 1); };
        AcceptButton = bNext;
        BuildPages();

        anim.Interval = 16;
        anim.Tick += delegate
        {
            shown += (target - shown) * 0.12;
            if (Math.Abs(target - shown) < 0.0005) shown = target;
            if (taskbar != null && installing) taskbar.Set(Handle, shown);
            if (page == 4) Invalidate(new Rectangle(0, Px(300), ClientSize.Width, ClientSize.Height - Px(370)));
        };
        Shown += delegate { taskbar = new TaskbarProgress(); anim.Start(); };
        GoTo(0);
    }

    Button MakeButton(string text, bool primary)
    {
        var b = new Button { Text = text, Font = fButton, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 90, 115);
        b.FlatAppearance.MouseOverBackColor = primary ? Accent2 : Color.FromArgb(30, 41, 59);
        b.BackColor = primary ? Accent : Field;
        b.ForeColor = primary ? Color.FromArgb(6, 16, 28) : Ink;
        b.Height = Px(36);
        Controls.Add(b);
        return b;
    }

    void LayoutButtons()
    {
        foreach (var b in new[] { bNext, bBack, bCancel })
            b.Width = Math.Max(Px(110), TextRenderer.MeasureText(b.Text, fButton).Width + Px(36));
        int y = ClientSize.Height - Px(58), x = ClientSize.Width - Px(40);
        foreach (var b in new[] { bNext, bBack, bCancel })
        {
            if (!b.Visible) continue;
            x -= b.Width;
            b.Location = new Point(x, y);
            x -= Px(10);
        }
    }

    // ---- page content (controls; titles and art are painted in OnPaint)
    Label AddText(int p, Font f, Color c, int x, int y, int w)
    {
        var l = new Label { Font = f, ForeColor = c, BackColor = Color.Transparent, AutoSize = true,
                            MaximumSize = new Size(w, 0), Location = new Point(x, y) };
        Controls.Add(l); pages[p].Add(l);
        return l;
    }

    void AddField(int p, TextBox t, int x, int y, int w)
    {
        t.Location = new Point(x, y); t.Width = w; t.Font = fBody;
        t.BackColor = Field; t.ForeColor = Ink; t.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(t); pages[p].Add(t);
    }

    Button Small(int p, int x, int y, int w)
    {
        var b = new Button { Font = fButton, FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Ink,
                             Cursor = Cursors.Hand, UseVisualStyleBackColor = false, Location = new Point(x, y),
                             Size = new Size(w, Px(28)) };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 90, 115);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 41, 59);
        Controls.Add(b); pages[p].Add(b);
        return b;
    }

    DarkCheckBox Check(int p, int x, int y, int w)
    {
        var c = new DarkCheckBox { Font = fBody, ForeColor = Ink, BackColor = Color.Transparent, AutoSize = true,
                                   MaximumSize = new Size(w, 0), Location = new Point(x, y) };
        Controls.Add(c); pages[p].Add(c);
        return c;
    }

    void BuildPages()
    {
        int x = Px(56), w = Px(500);

        // 0. language
        langLabel = AddText(0, fBody, Muted, x, Px(392), w);
        bEs = Small(0, x, Px(416), Px(120));
        bEn = Small(0, x + Px(130), Px(416), Px(120));
        bEs.Text = "Español"; bEn.Text = "English";
        bEs.Click += delegate { SetLanguage(false); };
        bEn.Click += delegate { SetLanguage(true); };

        // 1. ISO
        isoHelp = AddText(1, fBody, Muted, x, Px(196), w);
        AddField(1, tbIso, x, Px(262), w - Px(220));
        bIso = Small(1, x + w - Px(210), Px(261), Px(100));
        bIso.Click += delegate
        {
            using (var d = new OpenFileDialog { Title = T("ISO de Ace Combat 04 (EE. UU., SLUS-20152)", "Ace Combat 04 ISO (USA, SLUS-20152)"),
                                                Filter = T("Imagen ISO", "ISO image") + " (*.iso)|*.iso" })
                if (d.ShowDialog(this) == DialogResult.OK) { tbIso.Text = d.FileName; CheckIso(); }
        };
        // or a folder with the disc's files (SYSTEM.CNF, SLUS_201.52, DATA.CDP...)
        bIsoDir = Small(1, x + w - Px(100), Px(261), Px(100));
        bIsoDir.Click += delegate
        {
            using (var d = new FolderBrowserDialog { Description = T("Carpeta con los archivos del disco de Ace Combat 04 (SYSTEM.CNF, SLUS_201.52...)",
                                                                     "Folder with the Ace Combat 04 disc files (SYSTEM.CNF, SLUS_201.52...)"),
                                                     ShowNewFolderButton = false })
                if (d.ShowDialog(this) == DialogResult.OK) { tbIso.Text = d.SelectedPath; CheckIso(); }
        };
        tbIso.Leave += delegate { CheckIso(); };
        isoState = AddText(1, fBody, Muted, x, Px(302), w);

        // 2. folder
        dirHelp = AddText(2, fBody, Muted, x, Px(196), w);
        AddField(2, tbDir, x, Px(262), w - Px(110));
        tbDir.Text = @"C:\Juegos\Ace Combat 04";
        tbDir.TextChanged += delegate { CheckDir(); };
        bDir = Small(2, x + w - Px(100), Px(261), Px(100));
        bDir.Click += delegate
        {
            using (var d = new FolderBrowserDialog { Description = T("Carpeta para Ace Combat 04", "Folder for Ace Combat 04"), ShowNewFolderButton = true })
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    string chosen = d.SelectedPath;
                    // an empty or existing install folder is used as is; otherwise a subfolder
                    if (!InstallJob.IsInstall(chosen) && Directory.Exists(chosen) && Directory.EnumerateFileSystemEntries(chosen).Any())
                        chosen = Path.Combine(chosen, "Ace Combat 04");
                    tbDir.Text = chosen;
                }
        };
        dirState = AddText(2, fBody, Muted, x, Px(302), w);

        // 3. review
        depList = AddText(3, fBody, Ink, x, Px(190), w);
        ckTools = Check(3, x, Px(408), w);
        ckTools.Tag = "";
        ckTools.CheckedChanged += delegate { CheckDeps(false); };

        // 5. finished
        ckPlay = Check(5, x, Px(300), w);
        ckPlay.Checked = true;

        // credits: on the first and the last page
        foreach (int p in new[] { 0, 5 })
        {
            var bc = Small(p, x, p == 0 ? Px(340) : Px(392), Px(190));
            bc.Click += delegate { ShowCredits(); };
            bCredits.Add(bc);
        }
        ApplyTexts();
    }

    void SetLanguage(bool english)
    {
        L.En = english;
        isoChecked = null;   // re-word the ISO message
        ApplyTexts();
        GoTo(page);
    }

    // every visible word, in the chosen language
    void ApplyTexts()
    {
        Text = T("Instalar ", "Install ") + Product.Game + " (" + Product.Project + ")";
        bCancel.Text = T("Cancelar", "Cancel");
        bBack.Text = T("‹  Atrás", "‹  Back");
        langLabel.Text = T("Idioma del instalador y del juego:", "Setup and game language:");
        foreach (var b in new[] { bEs, bEn })
        {
            bool on = (b == bEn) == L.En;
            b.BackColor = on ? Accent : Field;
            b.ForeColor = on ? Color.FromArgb(6, 16, 28) : Ink;
            b.FlatAppearance.BorderSize = on ? 0 : 1;
        }
        isoHelp.Text = T("Ace Combat 04 necesita tu propia copia del juego: la ISO de la versión de EE. UU. (SLUS-20152). " +
                         "Elige el archivo .iso, o una carpeta con los archivos del disco (SYSTEM.CNF, SLUS_201.52...). " +
                         "El juego se juega desde ahí; el instalador no la copia ni la modifica.",
                         "Ace Combat 04 needs your own copy of the game: the ISO of the USA version (SLUS-20152). " +
                         "Choose the .iso file, or a folder with the disc's files (SYSTEM.CNF, SLUS_201.52...). " +
                         "The game plays from there; the setup neither copies nor changes it.");
        bIso.Text = T("ISO...", "ISO..."); bIsoDir.Text = T("Carpeta...", "Folder...");
        bDir.Text = T("Elegir...", "Browse...");
        dirHelp.Text = T("Elige dónde instalar el juego. Si eliges la carpeta de una instalación anterior, se actualiza " +
                         "y se conservan tus partidas, ajustes y mods.",
                         "Choose where to install the game. If you choose the folder of a previous installation, it is " +
                         "updated and your saves, settings and mods are kept.");
        ckPlay.Text = T("Abrir el juego al terminar (la primera vez se compila antes para tu PC)",
                        "Open the game when finished (the first time it is built for your PC first)");
        foreach (var b in bCredits) b.Text = T("Créditos y licencias", "Credits and licenses");
    }

    static string CreditsText()
    {
        string name = L.En ? "CREDITS.md" : "CREDITOS.md";
        try
        {
            using (var st = typeof(Wizard).Assembly.GetManifestResourceStream(name))
            using (var r = new StreamReader(st, Encoding.UTF8))
            {
                // light Markdown -> plain text
                var lines = r.ReadToEnd().Replace("\r", "").Split('\n')
                    .Select(l => l.StartsWith("## ") ? "\n" + l.Substring(3).ToUpperInvariant()
                               : l.StartsWith("# ") ? l.Substring(2).ToUpperInvariant() : l.Replace("**", "").Replace("*", "").Replace("`", ""));
                return string.Join("\r\n", lines.ToArray()).Trim();
            }
        }
        catch { return L.T("Créditos: ver CREDITOS.md en la carpeta del juego.", "Credits: see CREDITS.md in the game folder."); }
    }

    void ShowCredits()
    {
        using (var f = new Form { Text = T("Créditos y licencias", "Credits and licenses"), StartPosition = FormStartPosition.CenterParent,
                                  ClientSize = new Size(Px(620), Px(520)), MinimizeBox = false, MaximizeBox = false,
                                  FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = Color.FromArgb(12, 17, 27),
                                  ShowInTaskbar = false, Icon = Icon })
        {
            var t = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                                  Text = CreditsText(), Font = fBody, BackColor = Color.FromArgb(12, 17, 27), ForeColor = Ink,
                                  BorderStyle = BorderStyle.None };
            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Px(18)), BackColor = f.BackColor };
            pad.Controls.Add(t);
            var ok = MakeButton(T("Cerrar", "Close"), true);
            ok.Width = Px(110);
            Controls.Remove(ok);
            ok.Click += delegate { f.Close(); };
            var foot = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Px(58), FlowDirection = FlowDirection.RightToLeft,
                                             Padding = new Padding(Px(12)), BackColor = Color.FromArgb(9, 13, 21) };
            foot.Controls.Add(ok);
            f.Controls.Add(pad); f.Controls.Add(foot);
            f.AcceptButton = ok;
            f.Shown += delegate { t.SelectionLength = 0; ok.Focus(); };
            f.ShowDialog(this);
        }
    }

    // ---- navigation
    void GoTo(int p)
    {
        page = p;
        for (int i = 0; i < pages.Length; i++) foreach (var c in pages[i]) c.Visible = i == p;
        if (p == 1) CheckIso();
        if (p == 2) CheckDir();
        if (p == 3) CheckDeps(true);
        bBack.Visible = p > 0 && p < 4;
        bCancel.Visible = p < 5 && !(p == 4 && failed);
        bNext.Visible = p != 4 || failed;
        bNext.Text = p == 3 ? T("Instalar", "Install") : p == 5 ? T("Finalizar", "Finish")
                   : p == 4 ? T("Cerrar", "Close") : T("Siguiente  ›", "Next  ›");
        bNext.Enabled = p != 1 || isoOk != null;
        if (p == 3) bNext.Enabled = Deps.Windows64;
        LayoutButtons();
        Invalidate();
    }

    void Next()
    {
        if (page == 2 && !DirUsable()) return;
        if (page == 3)
        {
            if (!Deps.VulkanLoader &&
                MessageBox.Show(this, T("No se encontró Vulkan: el juego no arrancará hasta que instales el driver de tu tarjeta " +
                                        "gráfica.\n\n¿Instalar igual?",
                                        "Vulkan was not found: the game will not start until you install your graphics card " +
                                        "driver.\n\nInstall anyway?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            Install();
            return;
        }
        if (page == 4 && failed) { Close(); return; }
        if (page == 5) { Finish(); return; }
        GoTo(page + 1);
    }

    void CheckIso()
    {
        string path = tbIso.Text.Trim().Trim('"');
        if (path.Length == 0)
        {
            isoOk = null; isoChecked = null;
            isoState.ForeColor = Muted; isoState.Text = T("Elige el archivo .iso de tu disco, o una carpeta con sus archivos.",
                                                          "Choose your disc's .iso file, or a folder with its files.");
        }
        else if (path != isoChecked)
        {
            isoChecked = path; isoOk = null; isoError = null;
            try
            {
                InstallerCore.ValidateDisc(path);
                isoOk = path;
            }
            catch (Exception e)
            {
                isoError = e is FileNotFoundException || e is DirectoryNotFoundException
                    ? T("No existe ese archivo o carpeta.", "That file or folder does not exist.") : e.Message;
            }
            isoState.ForeColor = isoOk != null ? Ok : Bad;
            isoState.Text = isoOk != null ? T("✓  Ace Combat 04: Shattered Skies (EE. UU., SLUS-20152) verificado.",
                                              "✓  Ace Combat 04: Shattered Skies (USA, SLUS-20152) verified.")
                                          : "✗  " + isoError;
        }
        if (page == 1) bNext.Enabled = isoOk != null;
    }

    static string Gb(long bytes) { return (bytes / 1073741824.0).ToString("0.0") + " GB"; }

    // game + tools + build
    long NeedBytes
    {
        get
        {
            return (900L << 20) + (ckTools.Checked ? (900L << 20) : 0) + (2000L << 20);
        }
    }

    bool DirUsable()
    {
        string d = tbDir.Text.Trim();
        if (d.Length < 4 || !Path.IsPathRooted(d))
        { MessageBox.Show(this, T("Escribe una ruta completa, por ejemplo C:\\Juegos\\Ace Combat 04.", "Type a full path, for example C:\\Games\\Ace Combat 04."), Text); return false; }
        if (d.Length > 150)
        { MessageBox.Show(this, T("Usa una ruta más corta (la compilación necesita rutas cortas).", "Use a shorter path (the build needs short paths)."), Text); return false; }
        if (d.Any(c => c > 127))
        {
            MessageBox.Show(this, T("Usa una ruta sin tildes ni letras especiales (la compilación no las admite).",
                                    "Use a path without accents or special letters (the build does not support them)."), Text);
            return false;
        }
        try
        {
            string full = Path.GetFullPath(d);
            if (!InstallJob.IsInstall(full) && Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            {
                MessageBox.Show(this, T("Esa carpeta tiene otros archivos. Elige una carpeta vacía o una instalación anterior del juego.",
                                        "That folder has other files. Choose an empty folder or a previous installation of the game."), Text);
                return false;
            }
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, Text); return false; }
        return true;
    }

    void CheckDir()
    {
        string d = tbDir.Text.Trim();
        bool update = false;
        try { update = d.Length > 3 && InstallJob.IsInstall(d); } catch { }
        string root = "C:\\";
        try { root = Path.GetPathRoot(Path.GetFullPath(d.Length > 2 ? d : "C:\\")); } catch { }
        long free = Deps.FreeBytes(root);
        dirState.ForeColor = Muted;
        dirState.Text = (update ? T("✓  Hay una instalación aquí: se actualizará conservando tus partidas, ajustes y mods.\n\n",
                                    "✓  There is an installation here: it will be updated, keeping your saves, settings and mods.\n\n") : "") +
                        T("Espacio necesario: unos ", "Space needed: about ") + Gb(NeedBytes) +
                        T(" (juego, herramientas de compilación y la compilación).", " (game, build tools and the build).") +
                        (free >= 0 ? T("\nEspacio libre en ", "\nFree space on ") + root + ": " + Gb(free) : "");
        if (free >= 0 && free < NeedBytes) { dirState.ForeColor = Bad; dirState.Text += T("\n✗  No hay espacio suficiente.", "\n✗  Not enough space."); }
    }

    void CheckDeps(bool prompt)
    {
        var gpus = Deps.Gpus();
        int net = Deps.NetRelease;
        bool tools = Deps.ToolsReady;
        if (!tools && !"set".Equals(ckTools.Tag)) { ckTools.Tag = "set"; ckTools.Checked = true; }
        var sb = new StringBuilder();
        sb.Append(T("Juego:      ", "Game:       ")).Append(isoOk != null ? Path.GetFileName(isoOk) : T("(sin elegir)", "(not chosen)")).Append("\n");
        sb.Append(T("Carpeta:    ", "Folder:     ")).Append(tbDir.Text.Trim()).Append("\n");
        sb.Append(T("Idioma:     Español", "Language:   English")).Append("\n");
        sb.Append(T("Acceso:     escritorio, \"", "Shortcut:   desktop, \"")).Append(Product.LinkName).Append("\"\n");
        sb.Append(T("Mods:       incluidos y desactivados (Configurar AC4 > Mods)\n",
                    "Mods:       included, switched off (Configurar AC4 > Mods)\n"));
        sb.Append(T("            munición, dinero y guiado: pestaña \"Munición y dinero\"\n\n",
                    "            ammo, credits and guidance: \"Munición y dinero\" tab\n\n"));
        sb.Append(T("Tu PC:\n", "Your PC:\n"));
        Action<bool, string> row = (ok, what) => sb.Append(ok ? "✓  " : "✗  ").Append(what).Append("\n");
        row(Deps.Windows64, Deps.Windows64 ? T("Windows 10/11 de 64 bits", "64-bit Windows 10/11")
                                           : T("Hace falta Windows 10 u 11 de 64 bits", "64-bit Windows 10 or 11 is needed"));
        row(net >= 394802, net >= 394802 ? T(".NET Framework 4 (incluido en Windows)", ".NET Framework 4 (part of Windows)")
                                         : T("Falta .NET Framework 4.8", ".NET Framework 4.8 is missing"));
        row(Deps.VulkanLoader, (Deps.VulkanLoader ? "Vulkan" : T("Falta Vulkan: instala el driver de tu tarjeta gráfica",
                                                                 "Vulkan is missing: install your graphics card driver")) +
                               (gpus.Count > 0 ? " · " + gpus[0] : ""));
        row(true, T("Bibliotecas del juego (SDL3, C/C++, shaders): incluidas", "Game libraries (SDL3, C/C++, shaders): included"));
        row(tools || ckTools.Checked, tools ? T("Herramientas de compilación: ya instaladas", "Build tools: already installed")
                                            : ckTools.Checked ? T("Herramientas de compilación: se instalan (sin internet)", "Build tools: will be installed (no internet needed)")
                                                              : T("Sin herramientas: se usará la versión genérica del juego", "No build tools: the generic version of the game is used"));
        depList.Text = sb.ToString();
        ckTools.Visible = page == 3 && !tools;
        ckTools.Text = T("Instalar las herramientas para optimizar el juego para esta PC (recomendado)",
                         "Install the tools that optimize the game for this PC (recommended)");
        if (prompt && page == 3 && !Deps.VulkanLoader)
        {
            string url = Deps.DriverPage(gpus);
            if (MessageBox.Show(this, T("No se encontró Vulkan. ¿Abrir la página del driver de tu tarjeta gráfica?\n\n",
                                        "Vulkan was not found. Open the driver page of your graphics card?\n\n") + url, Text,
                                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                try { Process.Start(url); } catch { }
        }
    }

    async void Install()
    {
        installing = true; failed = false; error = null; shown = target = 0;
        GoTo(4);
        clock.Restart();
        cts = new CancellationTokenSource();
        job = new InstallJob { Iso = isoOk, Dest = tbDir.Text.Trim(), Tools = ckTools.Checked && !Deps.ToolsReady,
                               English = L.En, Cancel = cts.Token };
        job.Progress = (f, ph, d) => BeginInvoke((Action)(() => { if (f > target) target = f; phase = ph; detail = d ?? ""; }));
        try
        {
            await Task.Run(() => job.Run());
            installed = true;
            target = 1;
            installing = false;
            if (taskbar != null) taskbar.Set(Handle, 2);
            GoTo(5);
        }
        catch (Exception e)
        {
            installing = false;
            failed = true;
            error = e is OperationCanceledException
                ? T("Instalación cancelada. No se dejó nada a medio instalar en la carpeta nueva.",
                    "Installation cancelled. Nothing half-installed was left in the new folder.")
                : e.Message;
            if (taskbar != null) taskbar.Set(Handle, -1);
            GoTo(4);
        }
        finally { cts.Dispose(); cts = null; }
    }

    void Finish()
    {
        string dest = job.Dest, jugar = Path.Combine(dest, "Jugar.exe");
        Hide();
        // Only when the player leaves "open the game" ticked: Jugar.exe then
        // compiles first if the game has never been built on this PC (or the
        // hardware or driver changed), and opens it.  Unticked, nothing runs
        // now; the build happens the first time the player opens the game.
        if (ckPlay.Checked)
        {
            try
            {
                Process.Start(new ProcessStartInfo(jugar)
                              { WorkingDirectory = dest, UseShellExecute = true, Verb = "runas" });
            }
            catch { }   // UAC declined
        }
        Close();
    }

    // ---- painting
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int W = ClientSize.Width, H = ClientSize.Height, x = Px(56);
        if (bg != null)
        {
            float sc = Math.Max((float)W / bg.Width, (float)H / bg.Height);
            float iw = bg.Width * sc, ih = bg.Height * sc;
            g.DrawImage(bg, W - iw + Px(160), (H - ih) / 2f, iw, ih);
        }
        using (var lg = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.Black, Color.Black, LinearGradientMode.Horizontal))
        {
            var cb = new ColorBlend();
            cb.Colors = new[] { Color.FromArgb(253, 6, 10, 18), Color.FromArgb(240, 6, 10, 18), Color.FromArgb(130, 6, 10, 18), Color.FromArgb(50, 6, 10, 18) };
            cb.Positions = new[] { 0f, 0.5f, 0.78f, 1f };
            lg.InterpolationColors = cb;
            g.FillRectangle(lg, 0, 0, W, H);
        }
        using (var vg = new LinearGradientBrush(new Rectangle(0, H - Px(130), W, Px(131)), Color.FromArgb(0, 6, 10, 18),
                                                Color.FromArgb(235, 6, 10, 18), LinearGradientMode.Vertical))
            g.FillRectangle(vg, 0, H - Px(130), W, Px(130));

        using (var b = new SolidBrush(Accent)) g.FillRectangle(b, x, Px(58), Px(28), Px(3));
        Theme.DrawSpaced(g, "MOBIUS01  ·  ACE COMBAT 04  ·  SHATTERED SKIES", fLabel, Muted, x + Px(40), Px(50), 1.6f * s);

        // steps
        float sx = x;
        string[] steps = Steps;
        for (int i = 0; i < steps.Length; i++)
        {
            Color c = i == page ? Accent2 : i < page ? Muted : Color.FromArgb(80, 95, 115);
            string t = (i + 1) + "  " + steps[i];
            using (var b = new SolidBrush(c)) g.DrawString(t, fStep, b, sx, H - Px(48));
            float tw = g.MeasureString(t, fStep).Width;
            if (i == page) using (var b = new SolidBrush(Accent)) g.FillRectangle(b, sx + Px(2), H - Px(26), tw - Px(6), Px(2));
            sx += tw + Px(14);
        }

        string title, lead;
        switch (page)
        {
            case 0: title = T("Bienvenido", "Welcome");
                    lead = T("Instala Ace Combat 04 para PC en cuatro pasos. Solo necesitas tu ISO del juego.",
                             "Install Ace Combat 04 for PC in four steps. All you need is your ISO of the game."); break;
            case 1: title = T("Tu juego", "Your game");
                    lead = T("Elige la ISO de Ace Combat 04.", "Choose your Ace Combat 04 ISO."); break;
            case 2: title = T("Carpeta", "Folder"); lead = T("¿Dónde instalamos el juego?", "Where should the game go?"); break;
            case 3: title = T("Todo listo para instalar", "Ready to install");
                    lead = T("Revisa que esté todo bien y pulsa Instalar.", "Check that everything is right and press Install."); break;
            case 4: title = failed ? T("Algo salió mal", "Something went wrong") : T("Instalando", "Installing");
                    lead = failed ? "" : T("Esto tarda unos minutos. Puedes seguir usando la PC.", "This takes a few minutes. You can keep using the PC."); break;
            default: title = T("Todo listo", "All set");
                    lead = (job != null && job.Updated ? T("El juego se actualizó.", "The game was updated.") : T("El juego quedó instalado.", "The game is installed.")) +
                           T(" En el escritorio está el acceso \"", " Your desktop has the shortcut \"") + Product.LinkName + "\"." +
                           T(" Los mods vienen desactivados: actívalos cuando quieras en Configurar AC4 > Mods.",
                             " Mods come switched off: enable them whenever you like in Configurar AC4 > Mods."); break;
        }
        using (var b = new SolidBrush(Ink)) g.DrawString(title, fTitle, b, x - Px(3), Px(84));
        using (var b = new SolidBrush(failed && page == 4 ? Bad : Accent2))
            g.DrawString(failed && page == 4 ? error : lead, fLead, b, new RectangleF(x, Px(140), Px(560), Px(failed ? 150 : page == 5 ? 90 : 50)));

        if (page == 0)
            using (var b = new SolidBrush(Muted))
            {
                g.DrawString(T("•  Comprueba que tu ISO sea la versión de EE. UU. (SLUS-20152).\n" +
                               "•  Instala el juego y lo que necesita, con mods apagados (se activan a gusto).\n" +
                               "•  Si faltan, instala las herramientas para optimizar el juego para tu\n" +
                               "    procesador y tu tarjeta gráfica (no hace falta internet).\n" +
                               "•  Crea el acceso \"" + Product.LinkName + "\" en el escritorio.",
                               "•  Checks that your ISO is the USA version (SLUS-20152).\n" +
                               "•  Installs the game and what it needs, with mods switched off (enable any you like).\n" +
                               "•  If missing, installs the tools that optimize the game for your\n" +
                               "    processor and graphics card (no internet needed).\n" +
                               "•  Creates the \"" + Product.LinkName + "\" shortcut on your desktop."),
                             fBody, b, x, Px(196));
                g.DrawString(T("Mobius01 se basa en Ace Combat 5 Static Recompilation de sal063 y en Ace Combat 04 PC Port\n" +
                               "de PresidentRaptor y colaboradores. Ace Combat es una marca de Bandai Namco; no afiliado.",
                               "Mobius01 builds on sal063's Ace Combat 5 Static Recompilation and on PresidentRaptor's\n" +
                               "Ace Combat 04 PC Port and contributors. Ace Combat is a Bandai Namco trademark; not affiliated."),
                             fSmall, b, x, Px(300));
            }
        if (page == 4 && !failed) PaintProgress(g, x, W);
        if (page == 5)
            using (var b = new SolidBrush(Muted))
                g.DrawString(T("Carpeta: ", "Folder: ") + (job != null ? job.Dest : "") +
                             T("\nEl acceso directo abre Jugar.exe como administrador.", "\nThe shortcut opens Jugar.exe as administrator."),
                             fBody, b, x, Px(220));

        if (hoverMin) using (var b = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(b, rMin);
        if (hoverClose) using (var b = new SolidBrush(Color.FromArgb(220, 196, 43, 28))) g.FillRectangle(b, rClose);
        using (var pen = new Pen(Ink, Math.Max(1f, s)))
        {
            int cx = rClose.X + rClose.Width / 2, cy = rClose.Y + rClose.Height / 2, r = Px(5);
            g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
            g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
            int mx = rMin.X + rMin.Width / 2, my = rMin.Y + rMin.Height / 2;
            g.DrawLine(pen, mx - r, my, mx + r, my);
        }
    }

    void PaintProgress(Graphics g, int x, int W)
    {
        int H = ClientSize.Height, barY = H - Px(150), barW = W - 2 * x, barH = Px(8);
        string pct = ((int)Math.Floor(shown * 100 + 0.0001)) + "%";
        using (var b = new SolidBrush(Ink))
        {
            var sz = g.MeasureString(pct, fPct);
            g.DrawString(pct, fPct, b, x + barW - sz.Width + Px(6), barY - sz.Height - Px(2));
            g.DrawString(phase, fPhase, b, x, barY - Px(34));
        }
        using (var path = Theme.Rounded(new RectangleF(x, barY, barW, barH), barH / 2f))
        using (var b = new SolidBrush(Color.FromArgb(38, 255, 255, 255))) g.FillPath(b, path);
        float fw = (float)(barW * Math.Max(0, Math.Min(1, shown)));
        if (fw > barH)
        {
            var fill = new RectangleF(x, barY, fw, barH);
            using (var path = Theme.Rounded(fill, barH / 2f))
            {
                using (var lg = new LinearGradientBrush(new RectangleF(x, barY, Math.Max(fw, 1), barH), Accent, Accent2, LinearGradientMode.Horizontal))
                    g.FillPath(lg, path);
                float period = 1.6f, t = (float)(clock.Elapsed.TotalSeconds % period / period);
                float sw = Px(120), sx = x - sw + (fw + sw) * t;
                var old = g.Clip;
                g.SetClip(path);
                using (var sh = new LinearGradientBrush(new RectangleF(sx, barY, sw, barH), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
                {
                    var cb = new ColorBlend();
                    cb.Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) };
                    cb.Positions = new[] { 0f, 0.5f, 1f };
                    sh.InterpolationColors = cb;
                    g.FillRectangle(sh, sx, barY, sw, barH);
                }
                g.Clip = old;
            }
        }
        using (var b = new SolidBrush(Color.FromArgb(105, 120, 140)))
            g.DrawString(detail, fMono, b, new RectangleF(x, barY + Px(18), barW * 0.72f, Px(15)));
        double el = clock.Elapsed.TotalSeconds;
        string time = T("Tiempo ", "Time ") + Fmt(el) +
                      (shown > 0.05 && shown < 1 ? T("   ·   restante aprox. ", "   ·   about ") + Fmt(el * (1 - shown) / shown) + T("", " left") : "");
        using (var b = new SolidBrush(Muted))
        {
            var sz = g.MeasureString(time, fSmall);
            g.DrawString(time, fSmall, b, x + barW - sz.Width, barY + Px(18));
        }
    }

    static string Fmt(double secs)
    {
        int t = (int)Math.Max(0, Math.Round(secs));
        return t >= 60 ? string.Format("{0}:{1:00} min", t / 60, t % 60) : t + " s";
    }

    // ---- window chrome
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hc = rClose.Contains(e.Location), hm = rMin.Contains(e.Location);
        if (hc != hoverClose || hm != hoverMin) { hoverClose = hc; hoverMin = hm; Invalidate(new Rectangle(rMin.X, 0, rMin.Width * 2, rMin.Height)); }
        if (e.Button == MouseButtons.Left && !hc && !hm)
            Location = new Point(Location.X + e.X - drag.X, Location.Y + e.Y - drag.Y);
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); drag = e.Location; }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverClose = hoverMin = false; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (rMin.Contains(e.Location)) WindowState = FormWindowState.Minimized;
        else if (rClose.Contains(e.Location)) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (installing)
        {
            if (MessageBox.Show(this, T("¿Cancelar la instalación?", "Cancel the installation?"), Text,
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                if (cts != null) cts.Cancel();
                if (job != null) job.Abort();
            }
            e.Cancel = true;
            return;
        }
        if (page > 0 && page < 4 && !installed &&
            MessageBox.Show(this, T("¿Salir del instalador? El juego no se instalará.", "Leave the setup? The game will not be installed."), Text,
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        anim.Stop();
        base.OnFormClosing(e);
    }

    // "--preview <dir> [en]": renders each page to a PNG (nothing is installed)
    public void Preview(string dir)
    {
        Directory.CreateDirectory(dir);
        tbIso.Text = @"C:\Juegos\Ace Combat 04 (USA).iso";
        isoChecked = isoOk = tbIso.Text;
        isoState.ForeColor = Ok;
        isoState.Text = T("✓  Ace Combat 04: Shattered Skies (EE. UU., SLUS-20152) verificado.",
                          "✓  Ace Combat 04: Shattered Skies (USA, SLUS-20152) verified.");
        installed = true;
        Show();
        for (int p = 0; p < 6; p++)
        {
            if (p == 4) { phase = T("Instalando las herramientas de compilación", "Installing the build tools"); detail = "msys64/ucrt64/bin/cc1plus.exe"; shown = target = 0.62; clock.Restart(); }
            if (p == 5) job = new InstallJob { Dest = tbDir.Text };
            page = p;
            if (p == 3) CheckDeps(false);
            for (int i = 0; i < pages.Length; i++) foreach (var c in pages[i]) c.Visible = i == p;
            if (p == 3) { CheckDeps(false); ckTools.Visible = !Deps.ToolsReady; }
            bBack.Visible = p > 0 && p < 4; bCancel.Visible = p < 5; bNext.Visible = p != 4;
            bNext.Text = p == 3 ? T("Instalar", "Install") : p == 5 ? T("Finalizar", "Finish") : T("Siguiente  ›", "Next  ›");
            LayoutButtons();
            Invalidate();
            Application.DoEvents();
            using (var b = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                b.Save(Path.Combine(dir, p + "-" + Steps[p] + ".png"));
            }
        }
    }
}

static class SetupMain
{
    [STAThread]
    static int Main(string[] args)
    {
        Files.ModernPaths();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length > 1 && args[0] == "--preview")
        {
            if (args.Length > 2) L.En = args[2] == "en";
            using (var f = new Wizard()) f.Preview(args[1]);
            return 0;
        }
        // "--silent <iso> <folder> [tools] [en|es]": unattended install, for tests
        if (args.Length > 2 && args[0] == "--silent")
        {
            try
            {
                var job = new InstallJob { Iso = args[1], Dest = args[2], Tools = args.Contains("tools"),
                                           English = args.Contains("en") || (!args.Contains("es") && L.En),
                                           Cancel = CancellationToken.None };
                int last = -1;
                job.Progress = (f, ph, d) => { int p = (int)(f * 100); if (p != last) { last = p; Console.WriteLine(p + "% " + ph); } };
                job.Run();
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
        if (!Deps.Windows64)
        {
            MessageBox.Show(L.T("Ace Combat 04 necesita Windows 10 u 11 de 64 bits.", "Ace Combat 04 needs 64-bit Windows 10 or 11."),
                            Product.Project, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        Application.Run(new Wizard());
        return 0;
    }
}
#else
// ------------------------------------------------------------------ uninstall
static class UninstallMain
{
    [STAThread]
    static int Main(string[] args)
    {
        Files.ModernPaths();
        Application.EnableVisualStyles();
        string dir = args.Length > 2 && args[0] == "--from-temp" ? args[1] : Path.GetDirectoryName(Application.ExecutablePath);
        // the language chosen at install time
        try
        {
            string lang = Path.Combine(dir, @"VulkanRuntime\launcher-language.txt");
            if (File.Exists(lang)) L.En = !File.ReadAllText(lang).Trim().StartsWith("es", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        if (args.Length > 2 && args[0] == "--from-temp") return Remove(args[1], args[2] == "keep");

        string name = Product.Game + " (" + Product.Project + ")";
        if (!File.Exists(Path.Combine(dir, "Jugar.exe")))
        {
            MessageBox.Show(L.T("Desinstalar.exe debe estar en la carpeta del juego.", "Desinstalar.exe must be in the game folder."), name);
            return 1;
        }
        if (Process.GetProcessesByName("ac4").Length > 0 || Process.GetProcessesByName("Jugar").Length > 0)
        {
            MessageBox.Show(L.T("Cierra el juego antes de desinstalarlo.", "Close the game before uninstalling it."), name,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        if (MessageBox.Show(L.T("¿Desinstalar ", "Uninstall ") + name + L.T(" de\n", " from\n") + dir + "?", name,
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
        var keep = MessageBox.Show(L.T("¿Conservar tus partidas guardadas (VulkanRuntime\\saves)?\n\nSí: se quedan en la carpeta del juego.\nNo: se borran también.",
                                       "Keep your saved games (VulkanRuntime\\saves)?\n\nYes: they stay in the game folder.\nNo: they are deleted too."),
                                   name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (keep == DialogResult.Cancel) return 0;
        string tmp = Path.Combine(Path.GetTempPath(), "mobius01-uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Application.ExecutablePath, tmp);
        Process.Start(new ProcessStartInfo(tmp, "--from-temp \"" + dir + "\" " + (keep == DialogResult.Yes ? "keep" : "all"))
                      { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        return 0;
    }

    static int Remove(string dir, bool keepSaves)
    {
        Thread.Sleep(1500);   // let the first stage exit
        // only a folder that really is the game is touched
        if (!File.Exists(Path.Combine(dir, "Jugar.exe")) || !Directory.Exists(Path.Combine(dir, "VulkanRuntime"))) return 1;
        string saves = Path.Combine(dir, @"VulkanRuntime\saves");
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (keepSaves && f.StartsWith(saves + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
        }
        foreach (string d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        // what is left has paths over 260 characters (e.g. the compile's
        // build folder): the \\?\ form removes it; the saves stay
        foreach (string d in Directory.GetDirectories(dir))
        {
            if (keepSaves && saves.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string sub in Directory.GetDirectories(d))
                    if (!saves.StartsWith(sub, StringComparison.OrdinalIgnoreCase)) Files.DeleteLong(sub);
                continue;
            }
            Files.DeleteLong(d);
        }
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        try { File.Delete(Product.DesktopLink); } catch { }
        try { if (Directory.Exists(Product.StartMenuDir)) Directory.Delete(Product.StartMenuDir, true); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(Product.UninstallKey, false); } catch { }
        if (!Product.Test)
            MessageBox.Show(Product.Game + L.T(" se desinstaló.", " was uninstalled.") +
                            (keepSaves ? L.T("\n\nTus partidas quedaron en:\n", "\n\nYour saves are in:\n") + saves : "") +
                            L.T("\n\nLas herramientas de compilación (", "\n\nThe build tools (") + Product.MsysDir +
                            L.T(") no se tocan: puedes borrarlas a mano si no las usas.", ") are left as they are: delete them by hand if you do not use them."),
                            Product.Project, MessageBoxButtons.OK, MessageBoxIcon.Information);
        // remove this temporary copy once it exits
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del \"" + Application.ExecutablePath + "\"")
                      { CreateNoWindow = true, UseShellExecute = false });
        return 0;
    }
}
#endif
