// Per-PC compiler for AC4Recomp.  Jugar.exe (and "Guardar y jugar") call
// Builder.EnsureReady before every session.  The distributed game carries its
// source (VulkanRuntime\source) and a generic x86-64 ac4.exe to fall back on:
// on the first run on a PC, and whenever the processor, the graphics card or
// its driver, the recipe or the source change, it is compiled for that
// machine (-march=native, with the PGO profile) from a clean build folder.
// Missing dependencies (MSYS2, GCC, CMake, Ninja, SDL3, Vulkan, glslc) are
// installed first; the work runs elevated in a window with a 0-100% bar.
// On the developer's PC, AC4Recomp-desarrollo\COMPILACION_MANUAL.txt turns
// the automatic builds off (COMPILAR.bat still forces one).
//
// C# 5 / .NET Framework 4.x: compiled by build-config.bat with Config.cs.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

enum BuildResult { Ok = 0, Failed = 1, PlayPrevious = 2, Cancelled = 3 }

static class Builder
{
    // Bump when the build recipe changes: every PC then recompiles once.
    public const int RecipeVersion = 3;   // 3: -march=native again, compiled on each PC

    public static readonly string[] Packages = {
        "gcc", "cmake", "ninja", "pkgconf", "sdl3", "shaderc", "vulkan-headers", "vulkan-loader" };
    const string PkgPrefix = "mingw-w64-ucrt-x86_64-";
    const string MsysUrl = "https://repo.msys2.org/distrib/msys2-x86_64-latest.sfx.exe";

    public static string Root, Src, Runtime, Game, Logs, Msys, Ucrt;

    public static void Init(string root)
    {
        Root = root.TrimEnd('\\');
        // the distributed game keeps its source in VulkanRuntime\source; on
        // the developer's PC it sits next to the game, in AC4Recomp-desarrollo
        Src = Path.Combine(Root, "VulkanRuntime", "source");
        foreach (string cand in new[] {
                     Path.Combine(Root, "VulkanRuntime", "source"),
                     Path.Combine(Root, "VulkanSource"),
                     Path.Combine(Path.GetDirectoryName(Root) ?? Root, "AC4Recomp-desarrollo", "VulkanSource") })
            if (File.Exists(Path.Combine(cand, "CMakeLists.txt"))) { Src = cand; break; }
        Runtime = Path.Combine(Root, "VulkanRuntime", "runtime");
        if (!Directory.Exists(Runtime)) Runtime = Path.Combine(Root, "runtime");
        Game = Path.GetDirectoryName(Runtime);
        Logs = Path.Combine(Game, "logs");
        string m = Environment.GetEnvironmentVariable("AC4_MSYS");
        Msys = string.IsNullOrEmpty(m) ? @"C:\msys64" : m.TrimEnd('\\');
        Ucrt = Path.Combine(Msys, "ucrt64");
    }

    public static bool SourceAvailable
    {
        get { return File.Exists(Path.Combine(Src, "CMakeLists.txt")) && Directory.Exists(Path.Combine(Src, "generated")); }
    }
    public static string StampPath { get { return Path.Combine(Runtime, "ac4_build.stamp"); } }
    public static string GameExe { get { return Path.Combine(Runtime, "ac4.exe"); } }

    public static bool IsElevated
    {
        get
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    // ---------------------------------------------------------------- fingerprint

    public static SortedDictionary<string, string> Fingerprint()
    {
        var fp = new SortedDictionary<string, string>();
        fp["recipe"] = RecipeVersion.ToString();
        fp["cpu"] = CpuId();
        fp["gpu"] = GpuId();
        fp["toolchain"] = ToolchainId();
        fp["source"] = SourceHash();
        return fp;
    }

    static string CpuId()
    {
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                if (k != null)
                    return string.Format("{0} | {1} | {2} threads",
                        ((k.GetValue("ProcessorNameString") ?? "") + "").Trim(),
                        k.GetValue("Identifier"), Environment.ProcessorCount);
        }
        catch { }
        return "unknown | " + Environment.ProcessorCount;
    }

    static string GpuId()
    {
        var list = new List<string>();
        try
        {
            using (var cls = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
            {
                if (cls != null)
                    foreach (string sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length != 4 || !char.IsDigit(sub[0])) continue;
                        try
                        {
                            using (var k = cls.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                object desc = k.GetValue("DriverDesc"), ver = k.GetValue("DriverVersion");
                                if (desc == null) continue;
                                list.Add(desc + " " + ver);
                            }
                        }
                        catch { }
                    }
            }
        }
        catch { }
        list.Sort(StringComparer.Ordinal);
        return list.Count > 0 ? string.Join(" ; ", list.Distinct()) : "unknown";
    }

    static string ToolchainId()
    {
        string db = Path.Combine(Msys, "var", "lib", "pacman", "local");
        if (!Directory.Exists(db)) return "missing";
        var found = new List<string>();
        foreach (string d in Directory.GetDirectories(db))
        {
            string n = Path.GetFileName(d);
            foreach (string p in Packages)
                if (PackageName(n) == PkgPrefix + p) found.Add(n);
        }
        found.Sort(StringComparer.Ordinal);
        return found.Count > 0 ? string.Join(" ", found) : "missing";
    }

    // "mingw-w64-ucrt-x86_64-gcc-16.2.0-4" -> "mingw-w64-ucrt-x86_64-gcc"
    static string PackageName(string dir)
    {
        int b = dir.LastIndexOf('-');
        if (b <= 0) return dir;
        int a = dir.LastIndexOf('-', b - 1);
        return a <= 0 ? dir : dir.Substring(0, a);
    }

    public static List<string> MissingPackages()
    {
        var missing = new List<string>();
        string db = Path.Combine(Msys, "var", "lib", "pacman", "local");
        var have = new HashSet<string>();
        if (Directory.Exists(db))
            foreach (string d in Directory.GetDirectories(db)) have.Add(PackageName(Path.GetFileName(d)));
        foreach (string p in Packages)
            if (!have.Contains(PkgPrefix + p)) missing.Add(PkgPrefix + p);
        return missing;
    }

    public static bool MsysPresent
    {
        get { return File.Exists(Path.Combine(Msys, "usr", "bin", "bash.exe")) && File.Exists(Path.Combine(Msys, "usr", "bin", "pacman.exe")); }
    }

    // Everything that ends up in ac4.exe: path, size and modification time.
    static string SourceHash()
    {
        var files = new List<string>();
        foreach (string f in new[] { "CMakeLists.txt", Path.Combine("config", "ac4", "ac4_pipelines.seed.keys") })
            if (File.Exists(Path.Combine(Src, f))) files.Add(Path.Combine(Src, f));
        foreach (string d in new[] { "runtime", "generated", "third_party" })
        {
            string full = Path.Combine(Src, d);
            if (Directory.Exists(full)) files.AddRange(Directory.GetFiles(full, "*", SearchOption.AllDirectories));
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        using (var sha = SHA1.Create())
        {
            var sb = new StringBuilder();
            foreach (string f in files)
            {
                var fi = new FileInfo(f);
                sb.Append(f.Substring(Src.Length)).Append('|').Append(fi.Length).Append('|')
                  .Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
            }
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return files.Count + " files " + BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();
        }
    }

    static Dictionary<string, string> ReadStamp()
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (string line in File.ReadAllLines(StampPath))
            {
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
            }
        }
        catch { }
        return d;
    }

    public static void WriteStamp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Mobius01: what ac4.exe was compiled for. Delete this file to force a rebuild.");
        foreach (var kv in Fingerprint()) sb.Append(kv.Key).Append('=').AppendLine(kv.Value);
        sb.Append("built=").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        File.WriteAllText(StampPath, sb.ToString(), new UTF8Encoding(false));
    }

    // null when the installed ac4.exe was compiled on this PC, for this
    // hardware, driver, recipe and source.
    public static string RebuildReason()
    {
        if (!SourceAvailable) return null;
        if (!File.Exists(GameExe)) return "Todavía no hay una versión compilada del juego.";
        // AC4Recomp-desarrollo\COMPILACION_MANUAL.txt: the developer installs each
        // build by hand (COMPILAR.bat), so a source edit must not recompile on
        // play -- but new hardware or a new graphics driver still does
        bool manual = File.Exists(Path.Combine(Path.GetDirectoryName(Src) ?? Src, "COMPILACION_MANUAL.txt"));
        var old = ReadStamp();
        if (old.Count == 0) return "Primera ejecución en esta PC: se compila la mejor versión para tu procesador y tu sistema.";
        var now = Fingerprint();
        string v;
        if (!old.TryGetValue("cpu", out v) || v != now["cpu"]) return "Cambió el procesador: se compila para el nuevo hardware.";
        if (!old.TryGetValue("gpu", out v) || v != now["gpu"]) return "Cambió la tarjeta gráfica o su driver.";
        if (manual) return null;
        if (!old.TryGetValue("recipe", out v) || v != now["recipe"]) return "Nueva versión del compilador del port.";
        if (!old.TryGetValue("source", out v) || v != now["source"]) return "Cambió el código del port.";
        return null;
    }

    // ---------------------------------------------------------------- entry

    // Called before every session.  Returns false when the game must not start.
    public static bool EnsureReady(bool force, bool allowElevation)
    {
        if (string.IsNullOrEmpty(Root)) Init(AppDomain.CurrentDomain.BaseDirectory);
        if (!SourceAvailable) return true;               // prebuilt package: nothing to compile
        string reason = force ? "Compilación solicitada." : RebuildReason();
        if (reason == null) return true;

        if (allowElevation && !IsElevated && Environment.GetEnvironmentVariable("AC4_NO_ELEVATE") != "1")
        {
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath,
                    "--build-worker" + (force ? " --compilar" : ""));
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WorkingDirectory = Root;
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    var r = (BuildResult)p.ExitCode;
                    return r == BuildResult.Ok || r == BuildResult.PlayPrevious;
                }
            }
            catch (Win32Exception)
            {
                // elevation declined: compile without it (nothing here strictly needs it)
            }
        }
        var res = RunWindow(reason);
        return res == BuildResult.Ok || res == BuildResult.PlayPrevious;
    }

    public static BuildResult RunWindow(string reason)
    {
        using (var mx = new Mutex(false, "AC4Recomp.Builder"))
        {
            bool mine = false;
            try { mine = mx.WaitOne(0); } catch (AbandonedMutexException) { mine = true; }
            if (!mine)
            {
                MessageBox.Show("Ya hay una compilación de AC4 en curso.", "Ace Combat 04",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return BuildResult.Cancelled;
            }
            try
            {
                using (var f = new BuildForm(reason))
                {
                    // "Guardar y jugar" already runs inside the config window's loop
                    if (Application.MessageLoop) f.ShowDialog();
                    else Application.Run(f);
                    return f.Result;
                }
            }
            finally { mx.ReleaseMutex(); }
        }
    }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    public static void DpiAware() { try { SetProcessDPIAware(); } catch { } }
}

// ======================================================================== job

class BuildJob
{
    public Action<double, string, string> Progress;   // fraction 0..1, phase, detail
    public Action<string> Line;
    public volatile bool Cancel;
    public string LogPath;

    StreamWriter log;
    Process current;
    readonly object procLock = new object();

    struct Step { public string Name; public double Weight; public Action<Action<double>> Run; }

    void Log(string s)
    {
        if (log != null) lock (log) log.WriteLine(s);
        if (Line != null) Line(s);
    }

    public void Abort()
    {
        Cancel = true;
        lock (procLock)
        {
            if (current != null)
                try { Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + current.Id) { CreateNoWindow = true, UseShellExecute = false }); }
                catch { }
        }
    }

    public void Run()
    {
        Directory.CreateDirectory(Builder.Logs);
        LogPath = Path.Combine(Builder.Logs, "build-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        log = new StreamWriter(LogPath, false, new UTF8Encoding(false));
        log.AutoFlush = true;
        try
        {
            Log("Mobius01 build " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + (Builder.IsElevated ? " (elevated)" : ""));
            foreach (var kv in Builder.Fingerprint()) Log("  " + kv.Key + ": " + kv.Value);

            var steps = new List<Step>();
            // the setup installs only the UCRT64 packages (no bash/pacman): MSYS2
            // itself is needed only when a package is missing
            bool needMsys = !Builder.MsysPresent && Builder.MissingPackages().Count > 0;
            if (needMsys)
            {
                steps.Add(new Step { Name = "Descargando MSYS2", Weight = 10, Run = DownloadMsys });
                steps.Add(new Step { Name = "Instalando MSYS2", Weight = 8, Run = ExtractMsys });
                steps.Add(new Step { Name = "Actualizando MSYS2", Weight = 12, Run = UpdateMsys });
            }
            if (Builder.MissingPackages().Count > 0)
                steps.Add(new Step { Name = "Instalando dependencias", Weight = 18, Run = InstallPackages });
            steps.Add(new Step { Name = "Borrando la compilación anterior", Weight = 1.5, Run = Clean });
            steps.Add(new Step { Name = "Configurando para este hardware", Weight = 3, Run = Configure });
            steps.Add(new Step { Name = "Compilando el juego", Weight = 86, Run = Compile });
            steps.Add(new Step { Name = "Instalando", Weight = 2, Run = Install });
            steps.Add(new Step { Name = "Optimizando shaders para tu GPU", Weight = 4, Run = Precompile });

            double total = steps.Sum(s => s.Weight), done = 0;
            foreach (var st in steps)
            {
                if (Cancel) throw new OperationCanceledException();
                Log("== " + st.Name);
                double baseFrac = done / total, w = st.Weight / total;
                string name = st.Name;
                Report(baseFrac, name, "");
                st.Run(f => Report(baseFrac + w * Math.Max(0, Math.Min(1, f)), name, null));
                done += st.Weight;
            }
            Builder.WriteStamp();
            Log("build finished");
            Report(1, "Listo", "");
        }
        finally
        {
            log.Dispose();
            log = null;
        }
    }

    void Report(double f, string phase, string detail)
    {
        if (Progress != null) Progress(f, phase, detail);
    }

    // ------------------------------------------------------------ processes

    Dictionary<string, string> ToolEnv()
    {
        var env = new Dictionary<string, string>();
        env["PATH"] = Path.Combine(Builder.Ucrt, "bin") + ";" + Path.Combine(Builder.Msys, "usr", "bin") + ";" +
                      Environment.GetEnvironmentVariable("PATH");
        env["MSYSTEM"] = "UCRT64";
        env["CHERE_INVOKING"] = "1";
        return env;
    }

    int Exec(string exe, string args, string dir, Dictionary<string, string> env, Action<string> onLine)
    {
        Log("> " + exe + " " + args);
        var psi = new ProcessStartInfo(exe, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.WorkingDirectory = dir;
        psi.EnvironmentVariables.Remove("VULKAN_SDK");   // build against the MSYS2 Vulkan packages
        if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
        using (var p = new Process { StartInfo = psi })
        {
            DataReceivedEventHandler h = (s, e) =>
            {
                if (e.Data == null) return;
                Log(e.Data);
                if (onLine != null) onLine(e.Data);
            };
            p.OutputDataReceived += h;
            p.ErrorDataReceived += h;
            p.Start();
            lock (procLock) current = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            lock (procLock) current = null;
            if (Cancel) throw new OperationCanceledException();
            Log("exit code " + p.ExitCode);
            return p.ExitCode;
        }
    }

    void Must(int rc, string what)
    {
        if (rc != 0) throw new Exception(what + " (código " + rc + ")");
    }

    int Bash(string script, Action<string> onLine)
    {
        return Exec(Path.Combine(Builder.Msys, "usr", "bin", "bash.exe"), "-lc \"" + script.Replace("\"", "\\\"") + "\"",
                    Builder.Msys, ToolEnv(), onLine);
    }

    // ------------------------------------------------------------ dependencies

    string sfxPath;

    void DownloadMsys(Action<double> p)
    {
        sfxPath = Path.Combine(Path.GetTempPath(), "ac4-msys2-installer.sfx.exe");
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
        Log("download " + "https://repo.msys2.org/distrib/msys2-x86_64-latest.sfx.exe");
        using (var wc = new WebClient())
        using (var done = new ManualResetEvent(false))
        {
            Exception err = null;
            wc.DownloadProgressChanged += (s, e) =>
            {
                if (e.TotalBytesToReceive > 0) p((double)e.BytesReceived / e.TotalBytesToReceive);
                if (Cancel) wc.CancelAsync();
            };
            wc.DownloadFileCompleted += (s, e) => { err = e.Error; if (e.Cancelled) err = new OperationCanceledException(); done.Set(); };
            wc.DownloadFileAsync(new Uri("https://repo.msys2.org/distrib/msys2-x86_64-latest.sfx.exe"), sfxPath);
            done.WaitOne();
            if (err != null) throw new Exception("No se pudo descargar MSYS2: " + err.Message);
        }
    }

    void ExtractMsys(Action<double> p)
    {
        string parent = Path.GetDirectoryName(Builder.Msys);
        Directory.CreateDirectory(parent);
        int lines = 0;
        // 7-Zip self-extractor: creates <parent>\msys64.  A quoted path must not
        // end in a backslash (it would escape the closing quote).
        string o = parent.EndsWith("\\") ? parent : parent + "\\";
        string oarg = o.Contains(" ") ? "-o\"" + o + ".\"" : "-o" + o;
        Must(Exec(sfxPath, "-y " + oarg, parent, null, s => { lines++; p(Math.Min(0.95, lines / 3000.0)); }),
             "No se pudo instalar MSYS2");
        try { File.Delete(sfxPath); } catch { }
        if (!Builder.MsysPresent) throw new Exception("MSYS2 no quedó instalado en " + Builder.Msys);
    }

    void UpdateMsys(Action<double> p)
    {
        Bash("true", null);                         // first start: keyring and home directory
        p(0.2);
        Bash("pacman -Syuu --noconfirm --disable-download-timeout", null);   // may stop after core update
        p(0.6);
        Must(Bash("pacman -Syuu --noconfirm --disable-download-timeout", null), "No se pudo actualizar MSYS2");
        p(1);
    }

    void InstallPackages(Action<double> p)
    {
        var missing = Builder.MissingPackages();
        if (missing.Count == 0) return;
        var rx = new Regex(@"\((\s*\d+)/(\s*\d+)\)");
        int phase = 0;  // pacman: downloads then installs, each counted (n/m)
        Must(Bash("pacman -S --needed --noconfirm --disable-download-timeout " + string.Join(" ", missing), s =>
        {
            if (s.Contains("checking keyring") || s.Contains("loading packages")) phase = 1;
            var m = rx.Match(s);
            if (m.Success)
            {
                double n = double.Parse(m.Groups[1].Value.Trim()), t = double.Parse(m.Groups[2].Value.Trim());
                if (t > 0) p(phase == 0 ? 0.5 * n / t : 0.5 + 0.5 * n / t);
            }
        }), "No se pudieron instalar las dependencias");
        if (Builder.MissingPackages().Count > 0)
            throw new Exception("Faltan paquetes después de instalar: " + string.Join(", ", Builder.MissingPackages()));
    }

    // ------------------------------------------------------------ build

    string BuildDir { get { return Path.Combine(Builder.Src, "build"); } }
    string ReleaseDir { get { return Path.Combine(BuildDir, "release"); } }
    string PgoDir { get { return Path.Combine(Builder.Src, "pgo-profile"); } }

    void Clean(Action<double> p)
    {
        // keep a mission PGO profile that older builds left inside build\pgo
        string oldPgo = Path.Combine(BuildDir, "pgo", "pgo");
        if (Directory.Exists(oldPgo) && Directory.GetFiles(oldPgo, "*.gcda", SearchOption.AllDirectories).Length > 0
            && !Directory.Exists(PgoDir))
        {
            Log("keeping the PGO profile in " + PgoDir);
            CopyTree(oldPgo, PgoDir);
        }
        if (!Directory.Exists(BuildDir)) { p(1); return; }
        Log("deleting " + BuildDir);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                foreach (string f in Directory.GetFiles(BuildDir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(BuildDir, true);
                break;
            }
            catch (Exception e)
            {
                if (attempt >= 5) throw new Exception("No se pudo borrar la carpeta build: " + e.Message);
                Thread.Sleep(600);
            }
        }
        p(1);
    }

    static void CopyTree(string from, string to)
    {
        foreach (string d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(d.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, f.Replace(from, to), true);
    }

    static string Fwd(string p) { return p.Replace('\\', '/'); }

    bool usePgo;

    void Configure(Action<double> p)
    {
        usePgo = Directory.Exists(PgoDir) && Directory.GetFiles(PgoDir, "*.gcda", SearchOption.AllDirectories).Length > 0;
        if (Configure(usePgo) != 0)
        {
            if (!usePgo) throw new Exception("CMake no pudo configurar el proyecto");
            Log("PGO configuration failed; configuring without the profile");
            usePgo = false;
            Clean(x => { });
            Must(Configure(false), "CMake no pudo configurar el proyecto");
        }
        p(1);
    }

    int Configure(bool pgo)
    {
        string ucrt = Fwd(Builder.Ucrt);
        string args = string.Format(
            "-S \"{0}\" -B \"{1}\" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER={2}/bin/gcc.exe " +
            "-DCMAKE_CXX_COMPILER={2}/bin/c++.exe -DCMAKE_PREFIX_PATH={2} -DPS2_NATIVE_CPU=ON " +
            (pgo ? "-DPS2_PGO_MODE=use -DPS2_PGO_DIR=\"" + Fwd(PgoDir) + "\"" +
                   (File.Exists(Path.Combine(PgoDir, "FRESH")) ? " -DPS2_PGO_FRESH=ON" : "")
                 : "-DPS2_PGO_MODE=off"),
            Fwd(Builder.Src), Fwd(ReleaseDir), ucrt);
        return Exec(Path.Combine(Builder.Ucrt, "bin", "cmake.exe"), args, Builder.Src, ToolEnv(), null);
    }

    // The generated guest-code files need about 2 GB of RAM each to compile;
    // one job per thread runs a 16 GB PC out of memory (cc1: out of memory).
    int jobs;
    bool sawOom;

    static int JobsForMemory()
    {
        int threads = Math.Max(1, Environment.ProcessorCount);
        double freeGb = 4;
        try
        {
            using (var pc = new PerformanceCounter("Memory", "Available MBytes"))
                freeGb = pc.NextValue() / 1024.0;
        }
        catch { }
        // only the largest generated files need ~2 GB; out-of-memory retries halve this
        int byMem = (int)Math.Floor((freeGb - 0.5) / 1.5);
        return Math.Max(Math.Min(2, threads), Math.Min(threads, byMem));
    }

    void Compile(Action<double> p)
    {
        jobs = JobsForMemory();
        Log("parallel jobs: " + jobs);
        int rc;
        while ((rc = CompileOnce(p)) != 0 && sawOom && jobs > 1)
        {
            jobs = Math.Max(1, jobs / 2);
            Log("compiler ran out of memory; continuing with " + jobs + " parallel jobs");
        }
        if (rc == 0) return;
        if (sawOom) throw new Exception("No hay memoria suficiente para compilar: cierra otros programas y reintenta");
        if (!usePgo) throw new Exception("La compilación falló");
        Log("PGO build failed; rebuilding without the profile");
        usePgo = false;
        Clean(x => { });
        Must(Configure(false), "CMake no pudo configurar el proyecto");
        Must(CompileOnce(p), "La compilación falló");
    }

    // Progress is weighted by the size of each finished source file (the big
    // generated files dominate).  Between completions it creeps toward the
    // next completion so the bar keeps moving during the long files.
    int CompileOnce(Action<double> p)
    {
        var weights = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (string d in new[] { "generated", Path.Combine("runtime", "src"), "third_party" })
        {
            string full = Path.Combine(Builder.Src, d);
            if (!Directory.Exists(full)) continue;
            foreach (string f in Directory.GetFiles(full, "*.*", SearchOption.AllDirectories))
            {
                string e = Path.GetExtension(f).ToLowerInvariant();
                if (e != ".c" && e != ".cpp") continue;
                if (f.Contains(Path.Combine("third_party", "imgui")) && !Path.GetFileName(f).StartsWith("imgui")) continue;
                long w = new FileInfo(f).Length + 20000;
                weights[Fwd(f.Substring(Builder.Src.Length + 1))] = w;
                total += w;
            }
        }
        if (total == 0) total = 1;
        long finished = 0;
        double shown = 0;
        sawOom = false;
        var rx = new Regex(@"^\[(\d+)/(\d+)\]\s+(.*)$");
        var obj = new Regex(@"\.dir/(.+)\.obj$");
        DateTime last = DateTime.UtcNow;
        object gate = new object();
        bool running = true;

        var creep = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(250);
                lock (gate)
                {
                    if (!running) return;
                    double real = Math.Min(1.0, (double)finished / total);
                    double target = real + (1 - real) * 0.6;
                    double secs = (DateTime.UtcNow - last).TotalSeconds;
                    double creepTo = real + (target - real) * (1 - Math.Exp(-secs / 90.0));
                    if (creepTo > shown) { shown = creepTo; p(shown); }
                }
            }
        });
        creep.IsBackground = true;
        creep.Start();

        var env = ToolEnv();
        env["NINJA_STATUS"] = "[%f/%t] ";
        int rc;
        try
        {
            rc = Exec(Path.Combine(Builder.Ucrt, "bin", "cmake.exe"),
                      "--build \"" + Fwd(ReleaseDir) + "\" --target ac4 --parallel " + Math.Max(1, jobs),
                      Builder.Src, env, s =>
            {
                if (s.Contains("out of memory")) sawOom = true;
                var m = rx.Match(s);
                if (!m.Success) return;
                var o = obj.Match(m.Groups[3].Value.Trim());
                long w = 20000;
                if (o.Success)
                {
                    long ww;
                    if (weights.TryGetValue(o.Groups[1].Value, out ww)) w = ww;
                }
                else if (m.Groups[3].Value.Contains("Linking")) w = total / 50;
                lock (gate)
                {
                    finished += w;
                    last = DateTime.UtcNow;
                    double real = Math.Min(0.99, (double)finished / total);
                    if (real > shown) shown = real;
                    p(shown);
                }
            });
        }
        finally { lock (gate) running = false; }
        return rc;
    }

    void Install(Action<double> p)
    {
        string exe = Path.Combine(ReleaseDir, "ac4.exe");
        if (!File.Exists(exe)) throw new Exception("La compilación no produjo ac4.exe");
        Directory.CreateDirectory(Builder.Runtime);
        for (int attempt = 0; ; attempt++)
        {
            try { File.Copy(exe, Builder.GameExe, true); break; }
            catch (IOException)
            {
                if (attempt >= 10) throw new Exception("ac4.exe está en uso: cierra el juego y vuelve a intentarlo.");
                Thread.Sleep(500);
            }
        }
        p(0.3);
        string shaders = Path.Combine(Builder.Runtime, "shaders");
        Directory.CreateDirectory(shaders);
        foreach (string f in Directory.GetFiles(Path.Combine(ReleaseDir, "shaders")))
            File.Copy(f, Path.Combine(shaders, Path.GetFileName(f)), true);
        string seed = Path.Combine(Builder.Src, "config", "ac4", "ac4_pipelines.seed.keys");
        if (File.Exists(seed)) File.Copy(seed, Path.Combine(Builder.Runtime, "ac4_pipelines.seed.keys"), true);
        string image = Path.Combine(Builder.Src, "generated", "ps2_image.bin");
        if (File.Exists(image)) File.Copy(image, Path.Combine(Builder.Runtime, "ps2_image.bin"), true);
        p(0.6);
        CopyDlls(Builder.GameExe);
        p(1);
    }

    // Copies every MSYS2 DLL that ac4.exe needs (and what those DLLs need).
    void CopyDlls(string exe)
    {
        string bin = Path.Combine(Builder.Ucrt, "bin");
        string objdump = Path.Combine(bin, "objdump.exe");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(exe);
        var rx = new Regex(@"DLL Name:\s*(\S+)");
        while (queue.Count > 0)
        {
            string file = queue.Dequeue();
            var names = new List<string>();
            Exec(objdump, "-p \"" + file + "\"", bin, ToolEnv(), s =>
            {
                var m = rx.Match(s);
                if (m.Success) lock (names) names.Add(m.Groups[1].Value);
            });
            foreach (string n in names)
            {
                if (!seen.Add(n)) continue;
                // the Vulkan loader comes with the graphics driver (System32);
                // a private copy would bypass the driver's own loader and layers
                if (n.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(Path.Combine(Builder.Runtime, n)); } catch { }
                    continue;
                }
                string src = Path.Combine(bin, n);
                if (!File.Exists(src)) continue;          // a Windows system DLL
                string dst = Path.Combine(Builder.Runtime, n);
                try { File.Copy(src, dst, true); Log("dll " + n); }
                catch (IOException) { Log("dll " + n + " in use; kept"); }
                queue.Enqueue(src);
            }
        }
    }

    void Precompile(Action<double> p)
    {
        var env = new Dictionary<string, string>();
        env["PS2_SHADER_DIR"] = Path.Combine(Builder.Runtime, "shaders");
        int rc = Exec(Builder.GameExe, "--precompile-force", Builder.Runtime, env, null);
        if (rc != 0) Log("pipeline precompilation did not finish; it will happen while playing");
        p(1);
    }
}

// ======================================================================= window

class BuildForm : Form
{
    public BuildResult Result = BuildResult.Failed;

    readonly string reason;
    readonly float s;              // DPI scale
    readonly Image bg;
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
    readonly BuildJob job = new BuildJob();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly List<string> tail = new List<string>();
    readonly Font fLabel, fTitle, fReason, fPhase, fPct, fSmall, fMono, fButton;

    double target, shown;
    string phase = "Preparando", detail = "";
    bool failed, finished, hoverClose, hoverMin;
    string error;
    Rectangle rClose, rMin;
    Point drag;
    readonly List<Button> buttons = new List<Button>();
    double etaSeconds = -1;
    TaskbarProgress taskbar;

    static readonly Color Ink = Color.FromArgb(236, 242, 250);
    static readonly Color Muted = Color.FromArgb(150, 165, 185);
    static readonly Color Accent = Color.FromArgb(76, 182, 255);
    static readonly Color Accent2 = Color.FromArgb(132, 232, 255);
    static readonly Color Danger = Color.FromArgb(255, 96, 96);

    public BuildForm(string reason)
    {
        this.reason = reason;
        using (var g = CreateGraphics()) s = g.DpiX / 96f;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Px(960), Px(540));
        BackColor = Color.FromArgb(8, 12, 20);
        DoubleBuffered = true;
        Text = "Ace Combat 04: Shattered Skies";
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        try
        {
            var st = typeof(BuildForm).Assembly.GetManifestResourceStream("AC4.Background.jpg");
            if (st != null) bg = Image.FromStream(st);
        }
        catch { }

        fLabel = new Font("Segoe UI Semibold", 9f, GraphicsUnit.Point);
        fTitle = new Font("Segoe UI Semibold", 24f, GraphicsUnit.Point);
        fReason = new Font("Segoe UI", 11f, GraphicsUnit.Point);
        fPhase = new Font("Segoe UI Semibold", 11.5f, GraphicsUnit.Point);
        fPct = new Font("Segoe UI Light", 34f, GraphicsUnit.Point);
        fSmall = new Font("Segoe UI", 9f, GraphicsUnit.Point);
        fMono = new Font("Consolas", 8.5f, GraphicsUnit.Point);
        fButton = new Font("Segoe UI Semibold", 9.5f, GraphicsUnit.Point);

        rClose = new Rectangle(ClientSize.Width - Px(46), 0, Px(46), Px(36));
        rMin = new Rectangle(ClientSize.Width - Px(92), 0, Px(46), Px(36));

        anim.Interval = 16;
        anim.Tick += (o, e) =>
        {
            double t;
            lock (tail) t = target;
            shown += (t - shown) * 0.12;
            if (Math.Abs(t - shown) < 0.0005) shown = t;
            if (taskbar != null) taskbar.Set(Handle, failed ? -1 : shown);
            Invalidate();
        };

        job.Progress = (f, ph, d) =>
        {
            lock (tail)
            {
                if (f > target) target = f;
                if (ph != null) phase = ph;
                if (d != null) detail = d;
                double el = clock.Elapsed.TotalSeconds;
                if (target > 0.04 && target < 1) etaSeconds = el * (1 - target) / target;
            }
        };
        job.Line = line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (tail)
            {
                tail.Add(line.Length > 160 ? line.Substring(0, 160) : line);
                if (tail.Count > 4) tail.RemoveAt(0);
            }
        };
    }

    int Px(float v) { return (int)Math.Round(v * s); }

    // "--build-preview <dir>": renders the window states to PNGs (no build runs)
    public static void Preview(string dir)
    {
        Directory.CreateDirectory(dir);
        if (string.IsNullOrEmpty(Builder.Root)) Builder.Init(AppDomain.CurrentDomain.BaseDirectory);
        Action<string, Action<BuildForm>> shot = (name, setup) =>
        {
            try { ShotOne(dir, name, setup); }
            catch (Exception e) { File.WriteAllText(Path.Combine(dir, name + ".error.txt"), e.ToString()); }
        };
        Shots(shot);
    }

    static void ShotOne(string dir, string name, Action<BuildForm> setup)
    {
        {
            using (var f = new BuildForm("Cambió el código del port."))
            {
                f.CreateControl();
                var hc = f.Handle;
                setup(f);
                using (var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.ClientSize));
                    bmp.Save(Path.Combine(dir, name + ".png"));
                }
            }
        }
    }

    static void Shots(Action<string, Action<BuildForm>> shot)
    {
        shot("compilando", f =>
        {
            f.shown = f.target = 0.47; f.phase = "Compilando el juego"; f.etaSeconds = 104;
            f.tail.Add("[61/115] Building C object CMakeFiles/ac4.dir/generated/ps2_code_0004.c.obj");
            f.tail.Add("[62/115] Building C object CMakeFiles/ac4.dir/runtime/src/ps2_gs.c.obj");
            f.tail.Add("[63/115] Building CXX object CMakeFiles/ac4.dir/third_party/imgui/imgui.cpp.obj");
        });
        shot("error", f =>
        {
            f.shown = f.target = 0.31;
            f.Fail("La compilación falló (código 1)");
        });
        shot("listo", f => { f.shown = f.target = 1; f.finished = true; f.phase = "Listo. Iniciando el juego..."; });
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }  // CS_DROPSHADOW
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, Px(16), Px(16))); } catch { }
        try { taskbar = new TaskbarProgress(); } catch { taskbar = null; }
        anim.Start();
        StartJob();
    }

    void StartJob()
    {
        failed = false; finished = false; error = null;
        target = shown = 0; etaSeconds = -1;
        clock.Restart();
        foreach (var b in buttons) { Controls.Remove(b); b.Dispose(); }
        buttons.Clear();
        var t = new Thread(() =>
        {
            try
            {
                job.Cancel = false;
                job.Run();
                Post(Done);
            }
            catch (OperationCanceledException)
            {
                Post(() => { Result = BuildResult.Cancelled; Close(); });
            }
            catch (Exception ex)
            {
                Post(() => Fail(ex.Message));
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    void Post(Action a)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
    }

    void Done()
    {
        finished = true;
        phase = "Listo. Iniciando el juego...";
        target = 1;
        Result = BuildResult.Ok;
        var t = new System.Windows.Forms.Timer { Interval = 1100 };
        t.Tick += (o, e) => { t.Stop(); Close(); };
        t.Start();
    }

    void Fail(string msg)
    {
        failed = true;
        error = msg;
        phase = "No se pudo completar la compilación";
        Result = BuildResult.Failed;
        int x = Px(56), y = ClientSize.Height - Px(66);
        AddButton("Reintentar", ref x, y, true, (o, e) => StartJob());
        AddButton("Ver registro", ref x, y, false, (o, e) =>
        {
            try { Process.Start("notepad.exe", "\"" + job.LogPath + "\""); } catch { }
        });
        if (File.Exists(Builder.GameExe))
            AddButton("Jugar con la versión anterior", ref x, y, false, (o, e) => { Result = BuildResult.PlayPrevious; Close(); });
        AddButton("Salir", ref x, y, false, (o, e) => { Result = BuildResult.Failed; Close(); });
    }

    void AddButton(string text, ref int x, int y, bool primary, EventHandler click)
    {
        var b = new Button();
        b.Text = text;
        b.Font = fButton;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 90, 115);
        b.BackColor = primary ? Accent : Color.FromArgb(22, 30, 44);
        b.ForeColor = primary ? Color.FromArgb(6, 16, 28) : Ink;
        b.Cursor = Cursors.Hand;
        b.Height = Px(36);
        b.Width = TextRenderer.MeasureText(text, fButton).Width + Px(36);
        b.Location = new Point(x, y);
        b.Click += click;
        x += b.Width + Px(10);
        Controls.Add(b);
        buttons.Add(b);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int W = ClientSize.Width, H = ClientSize.Height;

        // background art, right-aligned, faded into the dark panel on the left
        if (bg != null)
        {
            float scale = Math.Max((float)W / bg.Width, (float)H / bg.Height);
            float iw = bg.Width * scale, ih = bg.Height * scale;
            g.DrawImage(bg, W - iw + Px(120), (H - ih) / 2f, iw, ih);
        }
        using (var lg = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(250, 6, 10, 18),
                                                Color.FromArgb(70, 6, 10, 18), LinearGradientMode.Horizontal))
        {
            var cb = new ColorBlend();
            cb.Colors = new[] { Color.FromArgb(252, 6, 10, 18), Color.FromArgb(235, 6, 10, 18), Color.FromArgb(120, 6, 10, 18), Color.FromArgb(60, 6, 10, 18) };
            cb.Positions = new[] { 0f, 0.38f, 0.72f, 1f };
            lg.InterpolationColors = cb;
            g.FillRectangle(lg, 0, 0, W, H);
        }
        using (var vg = new LinearGradientBrush(new Rectangle(0, H / 2 - 2, W, H / 2 + 4), Color.FromArgb(0, 6, 10, 18),
                                                Color.FromArgb(235, 6, 10, 18), LinearGradientMode.Vertical))
            g.FillRectangle(vg, 0, H / 2, W, H / 2);

        int x = Px(56);
        // header
        using (var b = new SolidBrush(Accent))
            g.FillRectangle(b, x, Px(58), Px(28), Px(3));
        DrawSpaced(g, "ACE COMBAT 04  ·  SHATTERED SKIES", fLabel, Muted, x + Px(40), Px(50), 1.6f * s);
        using (var b = new SolidBrush(Ink))
            g.DrawString(failed ? "Algo salió mal" : finished ? "Todo listo" : "Preparando el juego\npara esta PC",
                         fTitle, b, x - Px(3), Px(84));
        using (var b = new SolidBrush(failed ? Danger : Accent2))
            g.DrawString(failed ? error + "\n\nEl registro completo explica qué falló. Puedes reintentar o jugar con la versión ya instalada."
                                : reason, fReason, b,
                         new RectangleF(x, Px(failed ? 150 : 196), W * 0.55f, Px(failed ? 140 : 60)));
        if (!failed)
            using (var b = new SolidBrush(Muted))
                g.DrawString("El port se compila para tu procesador, tu GPU y su driver. Sólo vuelve a\n" +
                             "ocurrir si cambias de hardware, actualizas drivers o el port cambia.",
                             fSmall, b, x, Px(246));

        // progress
        int barY = H - Px(failed ? 150 : 118), barW = W - 2 * x, barH = Px(8);
        string pct = ((int)Math.Floor(shown * 100 + 0.0001)).ToString() + "%";
        using (var b = new SolidBrush(Ink))
        {
            var sz = g.MeasureString(pct, fPct);
            g.DrawString(pct, fPct, b, x + barW - sz.Width + Px(6), barY - sz.Height - Px(2));
            g.DrawString(phase, fPhase, b, x, barY - Px(34));
        }
        using (var path = Rounded(new RectangleF(x, barY, barW, barH), barH / 2f))
        using (var b = new SolidBrush(Color.FromArgb(38, 255, 255, 255)))
            g.FillPath(b, path);
        float fw = (float)(barW * Math.Max(0, Math.Min(1, shown)));
        if (fw > barH)
        {
            var fill = new RectangleF(x, barY, fw, barH);
            using (var path = Rounded(fill, barH / 2f))
            {
                using (var glow = new SolidBrush(Color.FromArgb(40, failed ? Danger : Accent)))
                using (var gp = Rounded(new RectangleF(x - Px(2), barY - Px(3), fw + Px(4), barH + Px(6)), barH))
                    g.FillPath(glow, gp);
                using (var lg = new LinearGradientBrush(new RectangleF(x, barY, Math.Max(fw, 1), barH),
                                                        failed ? Danger : Accent, failed ? Color.FromArgb(255, 150, 120) : Accent2,
                                                        LinearGradientMode.Horizontal))
                    g.FillPath(lg, path);
                if (!failed && !finished)
                {
                    // moving sheen
                    float period = 1.6f, t = (float)(clock.Elapsed.TotalSeconds % period / period);
                    float sw = Px(120), sx = x - sw + (fw + sw) * t;
                    var old = g.Clip;
                    g.SetClip(path);
                    using (var sh = new LinearGradientBrush(new RectangleF(sx, barY, sw, barH), Color.FromArgb(0, 255, 255, 255),
                                                            Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Horizontal))
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
        }

        if (!failed)
        {
            string time = "Tiempo " + Fmt(clock.Elapsed.TotalSeconds);
            if (!finished && etaSeconds >= 0) time += "   ·   restante aprox. " + Fmt(etaSeconds);
            using (var b = new SolidBrush(Muted))
            {
                var sz = g.MeasureString(time, fSmall);
                g.DrawString(time, fSmall, b, x + barW - sz.Width, barY + Px(18));
            }
            string[] lines;
            lock (tail) lines = tail.ToArray();
            using (var b = new SolidBrush(Color.FromArgb(105, 120, 140)))
                for (int i = 0; i < lines.Length && i < 3; i++)
                    g.DrawString(lines[lines.Length - 1 - i], fMono, b,
                                 new RectangleF(x, barY + Px(18) + i * Px(15), barW * 0.68f, Px(15)));
        }

        // window buttons
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

    static string Fmt(double secs)
    {
        int t = (int)Math.Max(0, Math.Round(secs));
        return t >= 60 ? string.Format("{0}:{1:00} min", t / 60, t % 60) : t + " s";
    }

    void DrawSpaced(Graphics g, string text, Font f, Color c, float x, float y, float spacing)
    {
        using (var b = new SolidBrush(c))
            foreach (char ch in text)
            {
                string cs = ch.ToString();
                g.DrawString(cs, f, b, x, y, StringFormat.GenericTypographic);
                x += (ch == ' ' ? g.MeasureString("n", f, PointF.Empty, StringFormat.GenericTypographic).Width * 0.6f
                                : g.MeasureString(cs, f, PointF.Empty, StringFormat.GenericTypographic).Width) + spacing;
            }
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hc = rClose.Contains(e.Location), hm = rMin.Contains(e.Location);
        if (hc != hoverClose || hm != hoverMin) { hoverClose = hc; hoverMin = hm; Invalidate(); }
        if (e.Button == MouseButtons.Left && !hc && !hm)
            Location = new Point(Location.X + e.X - drag.X, Location.Y + e.Y - drag.Y);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        drag = e.Location;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoverClose = hoverMin = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (rMin.Contains(e.Location)) WindowState = FormWindowState.Minimized;
        else if (rClose.Contains(e.Location)) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!failed && !finished && Result != BuildResult.Cancelled)
        {
            var r = MessageBox.Show(this, "¿Cancelar la compilación? El juego no se iniciará.", Text,
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
            Result = BuildResult.Cancelled;
            job.Abort();
        }
        anim.Stop();
        if (taskbar != null) taskbar.Set(Handle, 2);
        base.OnFormClosing(e);
    }

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
}

// Windows taskbar progress (ITaskbarList3).
class TaskbarProgress
{
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool full);
        void SetProgressValue(IntPtr hwnd, ulong done, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
    }
    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"), ClassInterface(ClassInterfaceType.None)]
    class CTaskbarList { }

    readonly ITaskbarList3 tb;
    public TaskbarProgress() { tb = (ITaskbarList3)new CTaskbarList(); tb.HrInit(); }

    // value 0..1 = progress, negative = error, >1 = clear
    public void Set(IntPtr hwnd, double v)
    {
        try
        {
            if (v > 1.5) { tb.SetProgressState(hwnd, 0); return; }
            if (v < 0) { tb.SetProgressState(hwnd, 4); tb.SetProgressValue(hwnd, 100, 100); return; }
            tb.SetProgressState(hwnd, 2);
            tb.SetProgressValue(hwnd, (ulong)(v * 1000), 1000);
        }
        catch { }
    }
}
