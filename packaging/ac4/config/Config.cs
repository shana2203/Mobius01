// Ace Combat 04 -- configuration launcher (graphics, display, controls, audio, cheats).
// Edits runtime\ac4_settings.ini (the same file the in-game F4 menu writes),
// keeps a cheat library and writes the enabled cheats as a PCSX2 .pnach mod,
// and starts the game the way Jugar.exe does.  C# 5 / .NET Framework 4.x so the
// compiler built into Windows can build it: see build-config.bat.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Ace Combat 04 - Configuracion")]
[assembly: System.Reflection.AssemblyProduct("Ace Combat 04 - Configuracion")]

// ---------------------------------------------------------------- settings file
// The display settings a new installation starts with: borderless fullscreen
// at the monitor's own resolution, the picture always 16:9 (widescreen patch,
// HUD stretched with it), vsync with the game clock locked to the monitor,
// FPS shown, the mouse pointer hidden.
static class Display
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
                                                 CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct DEVMODE
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2,
                   dmPanningWidth, dmPanningHeight;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

    // the primary monitor's real resolution (not scaled by Windows' DPI setting)
    public static Size Native()
    {
        try
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf(typeof(DEVMODE));
            if (EnumDisplaySettings(null, -1 /* ENUM_CURRENT_SETTINGS */, ref dm)
                && dm.dmPelsWidth >= 640 && dm.dmPelsHeight >= 480)
                return new Size(dm.dmPelsWidth, dm.dmPelsHeight);
        }
        catch { }
        var b = Screen.PrimaryScreen.Bounds;
        return b.Width >= 640 ? b.Size : new Size(1920, 1080);
    }

    public static void Apply(Ini ini)
    {
        Size s = Native();
        ini.SetInt("display", "window_mode", 1);          // borderless fullscreen
        ini.SetInt("display", "fullscreen_type", 1);
        ini.SetInt("display", "window_width", s.Width);
        ini.SetInt("display", "window_height", s.Height);
        ini.SetInt("display", "aspect", 3);               // 16:9
        ini.SetInt("display", "widescreen", 1);
        ini.SetInt("display", "hud_layout", 0);           // stretched with the picture
        ini.SetInt("display", "integer_scale", 0);
        ini.SetInt("display", "present_mode", 1);         // vsync (FIFO)
        ini.SetInt("display", "sync_to_display", 1);
        ini.SetInt("display", "fps_limit", 0);
        ini.SetInt("display", "show_fps", 1);
        ini.SetInt("display", "hide_cursor_fullscreen", 1);
    }

    // a settings file without display settings yet (first run): write them
    public static void FirstRun(string settingsPath)
    {
        try
        {
            var ini = new Ini(settingsPath);
            if (ini.Get("display", "window_mode", null) != null) return;
            Apply(ini);
            ini.Save();
        }
        catch { }
    }
}

class Ini
{
    public readonly string Path;
    readonly List<string> lines = new List<string>();

    public Ini(string path)
    {
        Path = path;
        if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));
    }

    static string Strip(string l)
    {
        int c = l.IndexOf(';');
        return (c >= 0 ? l.Substring(0, c) : l).Trim();
    }

    int Find(string section, string key, out int sectionEnd)
    {
        string cur = "";
        sectionEnd = -1;
        int found = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("[") && t.EndsWith("]")) { cur = t.Substring(1, t.Length - 2); continue; }
            if (cur != section) continue;
            if (t.Length > 0 && !t.StartsWith("#") && !t.StartsWith(";")) sectionEnd = i;
            else if (sectionEnd < 0) sectionEnd = i;
            string s = Strip(lines[i]);
            int eq = s.IndexOf('=');
            if (eq > 0 && s.Substring(0, eq).Trim() == key) found = i;
        }
        return found;
    }

    public string Get(string section, string key, string def)
    {
        int end;
        int i = Find(section, key, out end);
        if (i < 0) return def;
        string s = Strip(lines[i]);
        return s.Substring(s.IndexOf('=') + 1).Trim();
    }

    public int GetInt(string section, string key, int def)
    {
        int v;
        return int.TryParse(Get(section, key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
    }

    public float GetFloat(string section, string key, float def)
    {
        float v;
        return float.TryParse(Get(section, key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
    }

    public void Set(string section, string key, string value, string comment)
    {
        int end;
        int i = Find(section, key, out end);
        string keep = comment;
        if (i >= 0)
        {
            int c = lines[i].IndexOf(';');
            if (c >= 0 && comment == null) keep = lines[i].Substring(c + 1).Trim();
        }
        string line = key.PadRight(26) + " = " + value.PadRight(10) + (keep != null && keep.Length > 0 ? " ; " + keep : "");
        if (i >= 0) { lines[i] = line; return; }
        if (end < 0)
        {
            lines.Add("");
            lines.Add("[" + section + "]");
            lines.Add(line);
            return;
        }
        lines.Insert(end + 1, line);
    }

    public void SetInt(string section, string key, int v) { Set(section, key, v.ToString(CultureInfo.InvariantCulture), null); }
    public void SetFloat(string section, string key, float v) { Set(section, key, v.ToString("0.000000", CultureInfo.InvariantCulture), null); }

    public void Save()
    {
        if (lines.Count == 0 || !lines[0].StartsWith("#"))
        {
            lines.Insert(0, "# Ace Combat 04 recompiled -- settings.");
            lines.Insert(1, "# Written by the in-game menu (F4) or the configuration launcher.");
        }
        string tmp = Path + ".tmp";
        File.WriteAllLines(tmp, lines.ToArray());
        if (File.Exists(Path)) File.Delete(Path);
        File.Move(tmp, Path);
    }
}

// ---------------------------------------------------------- input name tables
static class Bind
{
    public static readonly string[] Actions = {
        "select", "l3", "r3", "start", "up", "right", "down", "left",
        "l2", "r2", "l1", "r1", "triangle", "circle", "cross", "square",
        "lstick_up", "lstick_down", "lstick_left", "lstick_right",
        "rstick_up", "rstick_down", "rstick_left", "rstick_right" };

    // the game's own default controls (ps2_settings_default_bindings), used
    // when the settings file has none yet: [action] = { primary, secondary }
    public static readonly int[,] DefaultKeys = {
        { 229, 0 }, { 0, 0 }, { 0, 0 }, { 40, 0 }, { 82, 0 }, { 79, 0 }, { 81, 0 }, { 80, 0 },
        { 30, 0 }, { 32, 0 }, { 20, 0 }, { 8, 0 }, { 4, 0 }, { 22, 0 }, { 27, 0 }, { 29, 0 },
        { 26, 96 }, { 90, 0 }, { 92, 0 }, { 94, 0 }, { 0, 0 }, { 0, 0 }, { 0, 0 }, { 0, 0 } };
    public static readonly int[,] DefaultPads = {
        { 5, 0 }, { 8, 0 }, { 9, 0 }, { 7, 0 }, { 12, 0 }, { 15, 0 }, { 13, 0 }, { 14, 0 },
        { 265, 0 }, { 267, 0 }, { 10, 0 }, { 11, 0 }, { 4, 0 }, { 2, 0 }, { 1, 0 }, { 3, 0 },
        { 258, 0 }, { 259, 0 }, { 256, 0 }, { 257, 0 }, { 262, 0 }, { 263, 0 }, { 260, 0 }, { 261, 0 } };

    public static readonly string[] ActionNames = {
        "Select", "L3", "R3", "Start", "Cruceta arriba", "Cruceta derecha", "Cruceta abajo", "Cruceta izquierda",
        "L2", "R2", "L1", "R1", "Triangulo", "Circulo", "Equis (X)", "Cuadrado",
        "Stick izq. arriba", "Stick izq. abajo", "Stick izq. izquierda", "Stick izq. derecha",
        "Stick der. arriba", "Stick der. abajo", "Stick der. izquierda", "Stick der. derecha" };

    // SDL3 gamepad buttons and axes, as the runtime stores them:
    // 1 + button, or 256 | axis << 1 | positive.
    static readonly string[] Buttons = {
        "Abajo (A / X)", "Derecha (B / Circulo)", "Izquierda (X / Cuadrado)", "Arriba (Y / Triangulo)",
        "Back / Select", "Guide", "Start", "Click stick izq.", "Click stick der.",
        "Bumper izq. (LB / L1)", "Bumper der. (RB / R1)",
        "Cruceta arriba", "Cruceta abajo", "Cruceta izquierda", "Cruceta derecha" };
    static readonly string[] Axes = { "Stick izq. X", "Stick izq. Y", "Stick der. X", "Stick der. Y", "Gatillo izq. (LT / L2)", "Gatillo der. (RT / R2)" };

    public static List<KeyValuePair<int, string>> PadChoices()
    {
        var l = new List<KeyValuePair<int, string>>();
        l.Add(new KeyValuePair<int, string>(0, "(ninguno)"));
        for (int b = 0; b < Buttons.Length; b++) l.Add(new KeyValuePair<int, string>(1 + b, Buttons[b]));
        for (int a = 0; a < Axes.Length; a++)
        {
            if (a >= 4) { l.Add(new KeyValuePair<int, string>(256 | (a << 1) | 1, Axes[a])); continue; }
            string neg = (a & 1) != 0 ? " arriba" : " izquierda", pos = (a & 1) != 0 ? " abajo" : " derecha";
            l.Add(new KeyValuePair<int, string>(256 | (a << 1), Axes[a] + neg));
            l.Add(new KeyValuePair<int, string>(256 | (a << 1) | 1, Axes[a] + pos));
        }
        return l;
    }

    public static string PadName(int code)
    {
        foreach (var kv in PadChoices()) if (kv.Key == code) return kv.Value;
        return "codigo " + code;
    }

    // Windows key -> SDL scancode
    static readonly Dictionary<Keys, int> keyToSdl = BuildKeys();
    static Dictionary<Keys, int> BuildKeys()
    {
        var d = new Dictionary<Keys, int>();
        for (int i = 0; i < 26; i++) d[Keys.A + i] = 4 + i;
        for (int i = 1; i <= 9; i++) d[Keys.D0 + i] = 29 + i;
        d[Keys.D0] = 39;
        d[Keys.Enter] = 40; d[Keys.Escape] = 41; d[Keys.Back] = 42; d[Keys.Tab] = 43; d[Keys.Space] = 44;
        d[Keys.OemMinus] = 45; d[Keys.Oemplus] = 46; d[Keys.OemOpenBrackets] = 47; d[Keys.OemCloseBrackets] = 48;
        d[Keys.OemPipe] = 49; d[Keys.OemSemicolon] = 51; d[Keys.OemQuotes] = 52; d[Keys.Oemtilde] = 53;
        d[Keys.Oemcomma] = 54; d[Keys.OemPeriod] = 55; d[Keys.OemQuestion] = 56; d[Keys.CapsLock] = 57;
        for (int i = 0; i < 12; i++) d[Keys.F1 + i] = 58 + i;
        d[Keys.Insert] = 73; d[Keys.Home] = 74; d[Keys.PageUp] = 75; d[Keys.Delete] = 76; d[Keys.End] = 77; d[Keys.PageDown] = 78;
        d[Keys.Right] = 79; d[Keys.Left] = 80; d[Keys.Down] = 81; d[Keys.Up] = 82;
        d[Keys.Divide] = 84; d[Keys.Multiply] = 85; d[Keys.Subtract] = 86; d[Keys.Add] = 87;
        for (int i = 1; i <= 9; i++) d[Keys.NumPad0 + i] = 88 + i;
        d[Keys.NumPad0] = 98; d[Keys.Decimal] = 99;
        d[Keys.LControlKey] = 224; d[Keys.LShiftKey] = 225; d[Keys.LMenu] = 226;
        d[Keys.RControlKey] = 228; d[Keys.RShiftKey] = 229; d[Keys.RMenu] = 230;
        return d;
    }

    public static int ToSdl(Keys k) { int v; return keyToSdl.TryGetValue(k, out v) ? v : 0; }

    public static string KeyName(int sc)
    {
        if (sc == 0) return "-";
        if (sc >= 4 && sc <= 29) return ((char)('A' + sc - 4)).ToString();
        if (sc >= 30 && sc <= 38) return ((char)('1' + sc - 30)).ToString();
        if (sc >= 58 && sc <= 69) return "F" + (sc - 57);
        if (sc >= 89 && sc <= 97) return "Num " + (sc - 88);
        switch (sc)
        {
            case 39: return "0"; case 40: return "Enter"; case 41: return "Esc"; case 42: return "Retroceso";
            case 43: return "Tab"; case 44: return "Espacio"; case 45: return "-"; case 46: return "=";
            case 47: return "["; case 48: return "]"; case 49: return "\\"; case 51: return ";"; case 52: return "'";
            case 53: return "`"; case 54: return ","; case 55: return "."; case 56: return "/"; case 57: return "Bloq Mayus";
            case 73: return "Insert"; case 74: return "Inicio"; case 75: return "RePag"; case 76: return "Supr";
            case 77: return "Fin"; case 78: return "AvPag"; case 79: return "Derecha"; case 80: return "Izquierda";
            case 81: return "Abajo"; case 82: return "Arriba"; case 84: return "Num /"; case 85: return "Num *";
            case 86: return "Num -"; case 87: return "Num +"; case 88: return "Num Enter"; case 98: return "Num 0";
            case 99: return "Num ."; case 224: return "Ctrl izq."; case 225: return "Shift izq."; case 226: return "Alt izq.";
            case 228: return "Ctrl der."; case 229: return "Shift der."; case 230: return "Alt der.";
        }
        return "tecla " + sc;
    }
}

// --------------------------------------------------------------------- cheats
class Cheat
{
    public string Name = "";
    public bool Enabled;
    public List<string> Patches = new List<string>();   // "place,EE,addr,type,value"
}

static class Cheats
{
    public static List<Cheat> Load(string path)
    {
        var list = new List<Cheat>();
        if (!File.Exists(path)) return list;
        Cheat cur = null;
        foreach (string raw in File.ReadAllLines(path))
        {
            string l = raw.Trim();
            if (l.Length == 0 || l.StartsWith("//") || l.StartsWith("#")) continue;
            if (l.StartsWith("[") && l.EndsWith("]"))
            {
                cur = new Cheat();
                cur.Name = l.Substring(1, l.Length - 2).Trim();
                list.Add(cur);
                continue;
            }
            if (cur == null) continue;
            if (l.StartsWith("enabled=")) { cur.Enabled = l.Substring(8).Trim() == "1"; continue; }
            if (l.StartsWith("patch=")) cur.Patches.Add(l.Substring(6).Trim());
        }
        return list;
    }

    public static void Save(string path, List<Cheat> list)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Biblioteca de cheats del launcher de configuracion (formato pnach).");
        sb.AppendLine("// Cada [seccion] es un cheat; enabled=1 lo activa en la proxima partida.");
        foreach (var c in list)
        {
            sb.AppendLine();
            sb.AppendLine("[" + c.Name + "]");
            sb.AppendLine("enabled=" + (c.Enabled ? "1" : "0"));
            foreach (var p in c.Patches) sb.AppendLine("patch=" + p);
        }
        File.WriteAllText(path, sb.ToString());
    }

    // The enabled cheats as a mod the runtime loads (mods\cheats).
    public static int WriteMod(string modsDir, List<Cheat> list)
    {
        string dir = System.IO.Path.Combine(modsDir, "cheats");
        Directory.CreateDirectory(dir);
        var on = list.Where(c => c.Enabled && c.Patches.Count > 0).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("gametitle=Ace Combat 04: Shattered Skies (SLUS-20152)");
        sb.AppendLine("// Generado por el launcher de configuracion. No editar: se reescribe al guardar.");
        foreach (var c in on)
        {
            sb.AppendLine();
            sb.AppendLine("// " + c.Name);
            foreach (var p in c.Patches) sb.AppendLine("patch=" + p);
        }
        File.WriteAllText(System.IO.Path.Combine(dir, "patch.pnach"), sb.ToString());
        File.WriteAllText(System.IO.Path.Combine(dir, "mod.toml"),
            "name     = \"Cheats (launcher)\"\n" +
            "author   = \"launcher de configuracion\"\n" +
            "priority = 100\n" +
            "enabled  = " + (on.Count > 0 ? "true" : "false") + "\n" +
            "game     = \"SLUS-20152\"\n");
        return on.Count;
    }

    // Accepts pnach lines (patch=...) and raw unencrypted codes:
    // 0aaaaaaa 000000vv (byte), 1aaaaaaa 0000vvvv (short), 2aaaaaaa vvvvvvvv (word).
    public static List<string> Parse(string text, List<string> errors)
    {
        var res = new List<string>();
        int n = 0;
        foreach (string raw in text.Split('\n'))
        {
            n++;
            string l = raw.Trim();
            if (l.Length == 0 || l.StartsWith("//") || l.StartsWith("#")) continue;
            if (l.StartsWith("patch=")) l = l.Substring(6).Trim();
            string[] f = l.Split(',');
            if (f.Length == 5)
            {
                res.Add(string.Join(",", f.Select(x => x.Trim()).ToArray()));
                continue;
            }
            string[] w = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            uint a, v;
            if (w.Length == 2 && w[0].Length == 8
                && uint.TryParse(w[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a)
                && uint.TryParse(w[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
            {
                uint type = a >> 28, addr = a & 0x0FFFFFFF;
                string t = type == 0 ? "byte" : type == 1 ? "short" : type == 2 ? "word" : null;
                if (t == null) { errors.Add("linea " + n + ": tipo de codigo " + type.ToString("X") + " no admitido (solo 0, 1 y 2: escrituras de datos)"); continue; }
                uint val = type == 0 ? v & 0xFF : type == 1 ? v & 0xFFFF : v;
                res.Add("1,EE," + addr.ToString("X8") + "," + t + "," + val.ToString("X8"));
                continue;
            }
            errors.Add("linea " + n + ": no es un parche pnach ni un codigo crudo de 8+8 cifras");
        }
        return res;
    }
}

// ----------------------------------------------------------------- key capture
class KeyCapture : Form
{
    public int Result = -1;   // -1 cancel, 0 clear, else SDL scancode
    public KeyCapture(string action)
    {
        Text = "Asignar tecla";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(360, 110);
        KeyPreview = true;
        var l = new Label();
        l.Text = "Pulsa la tecla para \"" + action + "\".\n\nEsc: cancelar     Supr: dejar sin tecla";
        l.Dock = DockStyle.Fill;
        l.TextAlign = ContentAlignment.MiddleCenter;
        Controls.Add(l);
        Theme.StyleDialog(this);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys k = keyData & Keys.KeyCode;
        if (k == Keys.Escape) { Result = -1; Close(); return true; }
        if (k == Keys.Delete) { Result = 0; Close(); return true; }
        // tell left and right modifiers apart
        if (k == Keys.ShiftKey) k = (GetAsyncKeyState(0xA1) & 0x8000) != 0 ? Keys.RShiftKey : Keys.LShiftKey;
        else if (k == Keys.ControlKey) k = (GetAsyncKeyState(0xA3) & 0x8000) != 0 ? Keys.RControlKey : Keys.LControlKey;
        else if (k == Keys.Menu) k = (GetAsyncKeyState(0xA5) & 0x8000) != 0 ? Keys.RMenu : Keys.LMenu;
        int sc = Bind.ToSdl(k);
        if (sc == 0) return true;
        Result = sc;
        Close();
        return true;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
}

// ------------------------------------------------------------------ main form
class ConfigForm : Form
{
    readonly string root, runtime, settingsPath, cheatsPath, modsDir, logsDir;
    Ini ini;
    List<Cheat> cheats;
    readonly ToolTip tips = new ToolTip();

    // graphics
    ComboBox cbRes, cbTexFilter, cbAniso, cbScale, cbWindow, cbAspect, cbHud, cbPresent;
    CheckBox ckFxaa, ckUiSmooth, ckHd, ckDeint, ckWide, ckSync, ckShowFps, ckInteger, ckHideCursor;
    TrackBar tbSharpen, tbBright, tbContrast, tbGamma, tbSat, tbRefl;
    NumericUpDown nuW, nuH, nuFps;
    // controls
    readonly int[,] keys = new int[24, 2];
    readonly int[,] pads = new int[24, 2];
    readonly Button[,] keyButtons = new Button[24, 2];
    readonly ComboBox[,] padBoxes = new ComboBox[24, 2];
    TrackBar tbDead, tbScale, tbBtnDead, tbVibe;
    ComboBox cbInvL, cbInvR, cbGlyphs, cbGamepad;
    readonly List<int> gamepadIds = new List<int>();

    // the controllers the game listed (ac4_gamepads.txt: "id|name") + the saved choice
    void LoadGamepadChoice()
    {
        int want = ini.GetInt("input", "gamepad", 0);
        cbGamepad.Items.Clear();
        gamepadIds.Clear();
        cbGamepad.Items.Add("Automatico (el primer mando conectado)");
        string list = Path.Combine(Path.GetDirectoryName(settingsPath), "ac4_gamepads.txt");
        try
        {
            if (File.Exists(list))
                foreach (string line in File.ReadAllLines(list))
                {
                    int bar = line.IndexOf('|'), id;
                    if (line.StartsWith("#") || bar <= 0 || !int.TryParse(line.Substring(0, bar), out id)) continue;
                    if (gamepadIds.Contains(id)) continue;
                    gamepadIds.Add(id);
                    cbGamepad.Items.Add(line.Substring(bar + 1));
                }
        }
        catch { }
        int at = gamepadIds.IndexOf(want);
        if (want != 0 && at < 0)
        {   // chosen before, not seen in the last run: keep it
            gamepadIds.Add(want);
            cbGamepad.Items.Add("Mando elegido (no conectado la ultima vez)");
            at = gamepadIds.Count - 1;
        }
        cbGamepad.SelectedIndex = want == 0 ? 0 : at + 1;
    }
    CheckBox ckVibe, ckBlockMenu;
    // audio
    TrackBar tbVolume;
    CheckBox ckMute;
    // cheats
    CheckedListBox clCheats;
    TextBox tbCheatCodes;

    public ConfigForm(string root)
    {
        this.root = root;
        runtime = Path.Combine(root, "VulkanRuntime", "runtime");
        if (!Directory.Exists(runtime)) runtime = Path.Combine(root, "runtime");
        string game = Path.GetDirectoryName(runtime);
        settingsPath = Path.Combine(runtime, "ac4_settings.ini");
        Display.FirstRun(settingsPath);
        cheatsPath = Path.Combine(game, "cheats.pnach");
        modsDir = Path.Combine(game, "mods");
        logsDir = Path.Combine(game, "logs");

        Text = "Ace Combat 04 - Configuración";
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.Base;
        ForeColor = Theme.Ink;
        DoubleBuffered = true;
        var work = Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(Math.Min(1060, work.Width - 20), Math.Min(720, work.Height - 20));
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(1, HeaderH, 1, 1);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        try
        {
            var st = typeof(ConfigForm).Assembly.GetManifestResourceStream("AC4.Background.jpg");
            if (st != null) art = Image.FromStream(st);
        }
        catch { }

        var pages = new[] { GameTab(), GraphicsTab(), DisplayTab(), ControlsTab(), AudioTab(), AmmoTab(), CheatsTab(), ModsTab() };
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(22, 14, 18, 8) };
        var nav = navBar = new NavBar { Dock = DockStyle.Left, Width = 200 };
        var views = new List<Control>();
        foreach (var p in pages)
        {
            var content = p.Controls[0];
            p.Controls.Remove(content);
            content.Dock = DockStyle.Fill;
            content.Visible = views.Count == 0;
            host.Controls.Add(content);
            views.Add(content);
            nav.Items.Add(p.Text);
            p.Dispose();
        }
        nav.Selected += i => { for (int v = 0; v < views.Count; v++) views[v].Visible = v == i; };

        var bottom = new FooterBar();
        bottom.Dock = DockStyle.Bottom;
        bottom.FlowDirection = FlowDirection.RightToLeft;
        bottom.WrapContents = false;
        bottom.Height = 68;
        bottom.Padding = new Padding(16, 16, 12, 16);
        var bPlay = Theme.MakeButton("Guardar y jugar", true, 160);
        bPlay.Click += delegate { if (SaveAll()) Play(); };
        var bSave = Theme.MakeButton("Guardar", false, 100);
        bSave.Click += delegate { if (SaveAll()) MessageBox.Show(this, "Configuración guardada.", Text); };
        var bClose = Theme.MakeButton("Cerrar", false, 90);
        bClose.Click += delegate { Close(); };
        var bDefaults = Theme.MakeButton("Valores por defecto", false, 150);
        bDefaults.Click += delegate { Defaults(); };
        bottom.Controls.AddRange(new Control[] { bPlay, bSave, bClose });
        bottom.Controls.Add(bDefaults);

        Controls.Add(host);
        Controls.Add(nav);
        Controls.Add(bottom);
        Theme.Style(host);
        // the footer buttons keep their own (primary/secondary) look
        AcceptButton = bPlay;
        LoadAll();
    }

    // ------------------------------------------------------------ window chrome
    const int HeaderH = 112;
    NavBar navBar;

    // "--config-preview <dir>": renders every page to a PNG (nothing is saved)
    public void Preview(string dir)
    {
        Directory.CreateDirectory(dir);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-4000, -4000);
        ShowInTaskbar = false;
        Show();
        for (int i = 0; i < navBar.Items.Count; i++)
        {
            navBar.SelectedIndex = i;
            Application.DoEvents();
            using (var bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
                bmp.Save(Path.Combine(dir, string.Format("{0}-{1}.png", i, navBar.Items[i])));
            }
        }
        Hide();
    }
    readonly Image art;
    Rectangle rClose, rMin;
    bool hoverClose, hoverMin;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;              // CS_DROPSHADOW
            cp.Style |= 0x20000;                    // WS_MINIMIZEBOX: minimise from the taskbar
            return cp;
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        rClose = new Rectangle(ClientSize.Width - 46, 0, 46, 34);
        rMin = new Rectangle(ClientSize.Width - 92, 0, 46, 34);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 14, 14)); } catch { }
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool c = rClose.Contains(e.Location), m = rMin.Contains(e.Location);
        if (c != hoverClose || m != hoverMin) { hoverClose = c; hoverMin = m; Invalidate(new Rectangle(rMin.X, 0, 92, 34)); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverClose || hoverMin) { hoverClose = hoverMin = false; Invalidate(new Rectangle(rMin.X, 0, 92, 34)); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (rClose.Contains(e.Location)) { Close(); return; }
        if (rMin.Contains(e.Location)) { WindowState = FormWindowState.Minimized; return; }
        if (e.Y < HeaderH) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }   // drag
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int W = ClientSize.Width;
        var head = new Rectangle(0, 0, W, HeaderH);
        g.SetClip(head);
        if (art != null)
        {
            // the jet, right-aligned and faded into the dark panel on the left
            float scale = Math.Max((float)W * 0.62f / art.Width, (float)HeaderH * 2.6f / art.Height);
            float iw = art.Width * scale, ih = art.Height * scale;
            g.DrawImage(art, W - iw + 60, HeaderH / 2f - ih * 0.47f, iw, ih);
        }
        using (var lg = new LinearGradientBrush(head, Theme.Base, Theme.Base, LinearGradientMode.Horizontal))
        {
            var cb = new ColorBlend();
            cb.Colors = new[] { Color.FromArgb(255, 8, 12, 20), Color.FromArgb(245, 8, 12, 20), Color.FromArgb(140, 8, 12, 20), Color.FromArgb(70, 8, 12, 20) };
            cb.Positions = new[] { 0f, 0.42f, 0.75f, 1f };
            lg.InterpolationColors = cb;
            g.FillRectangle(lg, head);
        }
        using (var vg = new LinearGradientBrush(new Rectangle(0, HeaderH / 2 - 1, W, HeaderH / 2 + 2),
                                                Color.FromArgb(0, 8, 12, 20), Color.FromArgb(200, 8, 12, 20), LinearGradientMode.Vertical))
            g.FillRectangle(vg, 0, HeaderH / 2, W, HeaderH - HeaderH / 2);
        g.ResetClip();

        using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 28, 30, 26, 3);
        Theme.DrawSpaced(g, "ACE COMBAT 04  ·  SHATTERED SKIES", Theme.Label, Theme.Muted, 64, 23, 1.5f);
        using (var b = new SolidBrush(Theme.Ink)) g.DrawString("Configuración", Theme.Title, b, 25, 44);
        using (var b = new SolidBrush(Theme.Accent2))
            g.DrawString("Gráficos, pantalla, controles, audio, mods y cheats", Theme.Body, b, 29, 84);
        using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, HeaderH - 1, W, HeaderH - 1);

        if (hoverMin) using (var b = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(b, rMin);
        if (hoverClose) using (var b = new SolidBrush(Color.FromArgb(220, 196, 43, 28))) g.FillRectangle(b, rClose);
        using (var pen = new Pen(Theme.Ink, 1f))
        {
            int cx = rClose.X + rClose.Width / 2, cy = rClose.Y + rClose.Height / 2, r = 5;
            g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
            g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
            int mx = rMin.X + rMin.Width / 2, my = rMin.Y + rMin.Height / 2;
            g.DrawLine(pen, mx - r, my, mx + r, my);
        }
        using (var pen = new Pen(Theme.Line)) g.DrawRectangle(pen, 0, 0, W - 1, ClientSize.Height - 1);
    }

    // -------------------------------------------------------------- helpers
    static TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel();
        t.Dock = DockStyle.Fill;
        t.AutoScroll = true;
        t.ColumnCount = 2;
        t.Padding = new Padding(2, 4, 12, 12);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    void Row(TableLayoutPanel t, string label, Control c, string tip)
    {
        var l = new Label();
        l.Text = label;
        l.AutoSize = true;
        l.Anchor = AnchorStyles.Left;
        l.Margin = new Padding(3, 8, 3, 3);
        c.Anchor = AnchorStyles.Left;
        if (c is ComboBox || c is TrackBar) c.Width = 330;
        t.Controls.Add(l);
        t.Controls.Add(c);
        if (tip != null) { tips.SetToolTip(l, tip); tips.SetToolTip(c, tip); }
    }

    void Header(TableLayoutPanel t, string text)
    {
        var l = new Label();
        l.Text = text;
        l.Font = Theme.Section;
        l.ForeColor = Theme.Accent2;
        l.Tag = "header";
        l.AutoSize = true;
        l.Margin = new Padding(3, t.Controls.Count == 0 ? 2 : 18, 3, 6);
        t.Controls.Add(l);
        t.SetColumnSpan(l, 2);
    }

    static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox();
        c.DropDownStyle = ComboBoxStyle.DropDownList;
        c.Items.AddRange(items);
        return c;
    }

    static CheckBox Check(string text) { var c = new DarkCheckBox(); c.Text = text; c.AutoSize = true; return c; }

    static TrackBar Slider(int min, int max)
    {
        var t = new TrackBar();
        t.Minimum = min; t.Maximum = max;
        t.TickFrequency = Math.Max(1, (max - min) / 10);
        t.AutoSize = false;
        t.Height = 32;
        return t;
    }

    TabPage Page(string title, Control content)
    {
        var p = new TabPage(title);
        p.Controls.Add(content);
        return p;
    }

    static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

    // ------------------------------------------------------------------ tabs
    readonly TextBox discPath = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
    string DiscPathFile { get { return Path.Combine(Path.GetDirectoryName(runtime), "launcher-iso.txt"); } }

    // ------------------------------------------------------------ save folder
    readonly TextBox savePath = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
    string SavesDirFile { get { return Path.Combine(Path.GetDirectoryName(runtime), "launcher-saves.txt"); } }
    string DefaultSavesDir { get { return Path.Combine(Path.GetDirectoryName(runtime), "saves"); } }

    string SavesDir()
    {
        string p = savePath.Text.Trim();
        return p.Length > 0 ? p : DefaultSavesDir;
    }

    static bool SamePath(string a, string b)
    {
        return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                             StringComparison.OrdinalIgnoreCase);
    }

    // Switch the save folder; offer to copy the current memory cards when the
    // new folder has none, so changing the folder never hides a campaign.
    void ChangeSavesDir(string dir)
    {
        string old = SavesDir();
        if (SamePath(dir, old)) return;
        try
        {
            Directory.CreateDirectory(dir);
            var cards = Directory.Exists(old)
                ? Directory.GetFiles(old, "card*.ps2").Concat(Directory.GetFiles(old, "card*.ps2mc")).ToArray()
                : new string[0];
            bool targetHasCards = Directory.GetFiles(dir, "card*.ps2").Length > 0 ||
                                  Directory.GetFiles(dir, "card*.ps2mc").Length > 0;
            if (cards.Length > 0 && !targetHasCards &&
                MessageBox.Show(this, "¿Copiar tus partidas actuales a la nueva carpeta?\n\n" + old + "\n→ " + dir +
                                "\n\nLa carpeta anterior no se modifica.", Text,
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                foreach (string c in cards) File.Copy(c, Path.Combine(dir, Path.GetFileName(c)), false);
            }
            else if (targetHasCards)
            {
                MessageBox.Show(this, "La carpeta elegida ya tiene partidas; el juego usará esas.", Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "No se pudo usar esa carpeta:\n" + e.Message, Text, MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
            return;
        }
        savePath.Text = Path.GetFullPath(dir);
    }

    void LoadSavesDir()
    {
        string p = File.Exists(SavesDirFile) ? File.ReadAllText(SavesDirFile).Trim() : "";
        savePath.Text = Path.GetFullPath(p.Length > 0 ? p : DefaultSavesDir);
    }

    void StoreSavesDir()
    {
        if (SamePath(SavesDir(), DefaultSavesDir)) { if (File.Exists(SavesDirFile)) File.Delete(SavesDirFile); }
        else File.WriteAllText(SavesDirFile, SavesDir());
    }

    TabPage GameTab()
    {
        var panel = Grid();
        panel.Controls.Add(new Label { Text = "Disco (ISO o carpeta)", AutoSize = true, Anchor = AnchorStyles.Left });
        panel.Controls.Add(discPath);
        var select = new Button { Text = "Seleccionar ISO...", AutoSize = true };
        // or a folder with the disc's files (SYSTEM.CNF, SLUS_201.52, DATA.CDP...)
        var selectDir = new Button { Text = "Seleccionar carpeta...", AutoSize = true };
        selectDir.Click += delegate
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Carpeta con los archivos del disco de Ace Combat 04 (SYSTEM.CNF, SLUS_201.52...)";
                dialog.ShowNewFolderButton = false;
                string current = FindDisc();
                if (current != null && Directory.Exists(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (!File.Exists(Path.Combine(dialog.SelectedPath, "SLUS_201.52")))
                {
                    MessageBox.Show(this, "Esa carpeta no tiene los archivos del disco de Ace Combat 04 (EE. UU.):\n" +
                                    "falta SLUS_201.52.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                discPath.Text = dialog.SelectedPath;
            }
        };
        select.Click += delegate
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Seleccionar la ISO de Ace Combat 04";
                dialog.Filter = "Imagen ISO (*.iso)|*.iso";
                dialog.CheckFileExists = true;
                string current = FindDisc();
                if (current != null && File.Exists(current)) dialog.FileName = current;
                if (dialog.ShowDialog(this) == DialogResult.OK) discPath.Text = dialog.FileName;
            }
        };
        var reset = new Button { Text = "Buscar automaticamente", AutoSize = true };
        reset.Click += delegate { discPath.Clear(); };
        var discButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        discButtons.Controls.AddRange(new Control[] { select, selectDir, reset });
        panel.Controls.Add(new Label { AutoSize = true });
        panel.Controls.Add(discButtons);

        Header(panel, "Partidas guardadas");
        panel.Controls.Add(new Label { Text = "Carpeta de partidas", AutoSize = true, Anchor = AnchorStyles.Left });
        panel.Controls.Add(savePath);
        var pickSaves = new Button { Text = "Elegir carpeta...", AutoSize = true };
        pickSaves.Click += delegate
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Carpeta donde el juego guarda las partidas (card0.ps2)";
                dialog.ShowNewFolderButton = true;
                string current = SavesDir();
                if (Directory.Exists(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                ChangeSavesDir(dialog.SelectedPath);
            }
        };
        var defaultSaves = new Button { Text = "Carpeta predeterminada", AutoSize = true };
        defaultSaves.Click += delegate { ChangeSavesDir(DefaultSavesDir); };
        var openSaves = new Button { Text = "Abrir carpeta", AutoSize = true };
        openSaves.Click += delegate
        {
            try { Directory.CreateDirectory(SavesDir()); Process.Start("explorer.exe", "\"" + SavesDir() + "\""); }
            catch (Exception e) { MessageBox.Show(this, e.Message, Text); }
        };
        var saveButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        saveButtons.Controls.Add(pickSaves);
        saveButtons.Controls.Add(defaultSaves);
        saveButtons.Controls.Add(openSaves);
        panel.Controls.Add(new Label { AutoSize = true });
        panel.Controls.Add(saveButtons);
        panel.Controls.Add(new Label { AutoSize = true });
        panel.Controls.Add(new Label {
            Text = "Las tarjetas de memoria (card0.ps2) se guardan en esta carpeta. Al cambiarla puedes " +
                   "copiar ahí tus partidas actuales; la carpeta anterior no se borra.",
            AutoSize = true, MaximumSize = new Size(650, 0), Tag = "muted", ForeColor = Theme.Muted });

        Header(panel, "Idioma");
        cbLang = Combo("English (original)", "Español neutro");
        Row(panel, "Idioma del juego", cbLang,
            "Subtítulos de la radio y de las cinemáticas, briefings, menús y descripciones. " +
            "Se aplica al iniciar el juego.");
        Builder.Init(root);
        // installed in English: the Spanish mod is made from the disc the next
        // time the game starts, if Spanish is chosen here
        bool canMake = !File.Exists(LangModManifest) && Builder.CanMakeSpanish;
        if (!File.Exists(LangModManifest) && !canMake) cbLang.Enabled = false;
        var languageNote = new Label {
            Text = File.Exists(LangModManifest)
                ? "El español se aplica al iniciar el juego; cambiarlo en plena partida requiere reiniciar."
                : canMake
                ? "Al elegir español, la traducción se genera desde tu disco la próxima vez que juegues (unos minutos, una sola vez)."
                : "La traducción al español no está instalada (falta VulkanRuntime\\mods\\espanol).",
            AutoSize = true, MaximumSize = new Size(650, 0), Tag = "muted", ForeColor = Theme.Muted };
        panel.Controls.Add(new Label { AutoSize = true });
        panel.Controls.Add(languageNote);

        Header(panel, "Compilación");
        Builder.Init(root);
        var buildInfo = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Tag = "muted", ForeColor = Theme.Muted };
        Action showInfo = () => { buildInfo.Text = BuildStatusText(); };
        showInfo();
        var compile = new Button { Text = "Compilar para esta PC", AutoSize = true };
        compile.Enabled = Builder.SourceAvailable;
        compile.Click += delegate
        {
            if (MessageBox.Show(this, "Se compilará el juego para el procesador, la tarjeta gráfica y el " +
                    "sistema de esta PC, desde cero. Tarda unos minutos (más si hay que instalar las " +
                    "herramientas) y pide permisos de administrador.\n\n¿Compilar ahora?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            bool ok = Builder.EnsureReady(true, true);
            showInfo();
            MessageBox.Show(this, ok ? "Compilación terminada e instalada." : "La compilación no se completó; " +
                            "se sigue usando la versión anterior.", Text, MessageBoxButtons.OK,
                            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        };
        Row(panel, "Versión del juego", buildInfo, null);
        panel.Controls.Add(new Label { AutoSize = true });
        panel.Controls.Add(compile);
        return Page("Juego", panel);
    }

    static string BuildStatusText()
    {
        if (!Builder.SourceAvailable)
            return "Esta copia no incluye el código fuente: usa la versión ya compilada.";
        try
        {
            if (File.Exists(Builder.StampPath))
            {
                string cpu = null, built = null;
                foreach (string l in File.ReadAllLines(Builder.StampPath))
                {
                    if (l.StartsWith("cpu=")) cpu = l.Substring(4).Split('|')[0].Trim();
                    if (l.StartsWith("built=")) built = l.Substring(6);
                }
                string why = Builder.RebuildReason();
                return "Compilada " + (built ?? "?") + " para " + (cpu ?? "?") + "." +
                       (why != null ? "\nSe volverá a compilar al jugar: " + why : "");
            }
        }
        catch { }
        return "Todavía no se compiló en esta PC: se hará al jugar por primera vez.";
    }

    // ------------------------------------------------------------- language
    ComboBox cbLang;
    string LangFile { get { return Path.Combine(Path.GetDirectoryName(runtime), "launcher-language.txt"); } }
    string LangModManifest { get { return Path.Combine(modsDir, "espanol", "mod.toml"); } }

    void LoadLanguage()
    {
        string lang = File.Exists(LangFile) ? File.ReadAllText(LangFile).Trim().ToLowerInvariant() : "en";
        cbLang.SelectedIndex = lang.StartsWith("es") && (File.Exists(LangModManifest) || Builder.CanMakeSpanish) ? 1 : 0;
    }

    // The language is the "espanol" mod switched on or off (its manifest's
    // header line "enabled = ..."), plus launcher-language.txt for the launcher.
    void SaveLanguage()
    {
        bool es = cbLang.SelectedIndex == 1;
        File.WriteAllText(LangFile, es ? "es" : "en");
        SetModEnabled(LangModManifest, es);
    }

    static void SetModEnabled(string path, bool enabled)
    {
        if (!File.Exists(path)) return;
        string manifest = File.ReadAllText(path);
        int end = ManifestHeaderEnd(manifest);
        var setting = modEnabled.Match(manifest.Substring(0, end));
        if (enabled == (!setting.Success || setting.Groups[1].Value == "true")) return;
        string value = enabled ? "true" : "false";
        if (setting.Success)
            manifest = manifest.Remove(setting.Groups[1].Index, setting.Groups[1].Length)
                               .Insert(setting.Groups[1].Index, value);
        else
            manifest = "enabled = " + value + Environment.NewLine + manifest;
        string temp = path + ".launcher.tmp";
        File.WriteAllText(temp, manifest, new UTF8Encoding(false));
        File.Replace(temp, path, null);
    }

    TabPage GraphicsTab()
    {
        var t = Grid();
        Header(t, "Resolucion y filtrado");
        cbRes = Combo("Auto (cubrir la ventana)", "Nativa 640x448", "2x (1280x896)", "3x (1920x1344)", "4x (2560x1792)",
                      "5x (3200x2240)", "6x (3840x2688)", "7x (4480x3136)", "8x (5120x3584)");
        Row(t, "Resolucion interna", cbRes, "Resolucion a la que se dibuja el juego. Mayor = mas nitido y mas carga de GPU.");
        cbScale = Combo("Cercano - pixeles exactos", "Bilineal - suave", "Bilineal nitido - pixeles parejos", "AMD FSR 1.0 - reescalado con bordes");
        Row(t, "Filtro de escalado", cbScale, "Como se ajusta la imagen a la pantalla. FSR 1.0 solo actua cuando la resolucion interna es menor que la de la pantalla (p. ej. Nativa o 2x en 1080p); su nitidez es el control 'Nitidez'.");
        cbTexFilter = Combo("Segun el juego", "Forzar cercano", "Forzar bilineal");
        Row(t, "Filtro de texturas", cbTexFilter, null);
        cbAniso = Combo("Desactivado", "2x", "4x", "8x", "16x");
        Row(t, "Filtrado anisotropico", cbAniso, null);
        ckUiSmooth = Check("Suavizar texturas de la interfaz");
        Row(t, "", ckUiSmooth, null);
        tbRefl = Slider(0, 100);
        Row(t, "Reflejo de los aviones", tbRefl, "Brillo metalico de los aviones. 100 es el de PS2; en aviones plateados como el F-15C parece una segunda imagen del avion.");
        ckHd = Check("Usar pack de texturas HD (textures\\hd)");
        Row(t, "", ckHd, null);
        ckDeint = Check("Desentrelazar (unir los dos campos)");
        Row(t, "", ckDeint, null);
        Header(t, "Postproceso");
        ckFxaa = Check("FXAA (suavizado de bordes)");
        Row(t, "", ckFxaa, null);
        tbSharpen = Slider(0, 100);
        Row(t, "Nitidez", tbSharpen, "Con FSR 1.0 es la intensidad de RCAS.");
        tbBright = Slider(-50, 50);
        Row(t, "Brillo", tbBright, null);
        tbContrast = Slider(50, 150);
        Row(t, "Contraste", tbContrast, null);
        tbGamma = Slider(50, 200);
        Row(t, "Gamma", tbGamma, null);
        tbSat = Slider(0, 200);
        Row(t, "Saturacion", tbSat, null);
        return Page("Graficos", t);
    }

    TabPage DisplayTab()
    {
        var t = Grid();
        Header(t, "Ventana");
        cbWindow = Combo("Ventana", "Pantalla completa sin bordes", "Pantalla completa exclusiva");
        Row(t, "Modo", cbWindow, "F11 o Alt+Enter cambian durante el juego.");
        nuW = new NumericUpDown(); nuW.Minimum = 320; nuW.Maximum = 16384;
        Row(t, "Ancho de la ventana", nuW, null);
        nuH = new NumericUpDown(); nuH.Minimum = 224; nuH.Maximum = 16384;
        Row(t, "Alto de la ventana", nuH, null);
        Header(t, "Imagen");
        cbAspect = Combo("Estirar a la ventana", "Auto (4:3, o 16:9 con panoramica)", "4:3", "16:9", "Personalizado");
        Row(t, "Relacion de aspecto", cbAspect, null);
        ckWide = Check("Parche panoramico 16:9");
        Row(t, "", ckWide, null);
        cbHud = Combo("Estirado con la imagen", "4:3 en el centro", "4:3 con esquinas en los bordes (AC4)");
        Row(t, "HUD en panoramico", cbHud, null);
        ckInteger = Check("Escalado entero (sin reescalado desigual)");
        Row(t, "", ckInteger, null);
        Header(t, "Presentacion y ritmo");
        cbPresent = Combo("Mailbox - baja latencia, sin tearing", "VSync (FIFO)", "Inmediato - tearing, minima latencia", "VSync adaptativo");
        Row(t, "Presentacion", cbPresent, null);
        ckSync = Check("Sincronizar el reloj del juego con el monitor (60/120 Hz)");
        Row(t, "", ckSync, "Movimiento uniforme en monitores de 60 o 120 Hz. Sin efecto en otros refrescos.");
        nuFps = new NumericUpDown(); nuFps.Minimum = 0; nuFps.Maximum = 240;
        Row(t, "Limite de FPS (0 = sin limite)", nuFps, null);
        ckShowFps = Check("Mostrar FPS");
        Row(t, "", ckShowFps, null);
        ckHideCursor = Check("Esconder el puntero del mouse en pantalla completa");
        Row(t, "", ckHideCursor, "Vuelve a aparecer mientras el menu F4 esta abierto.");
        return Page("Pantalla", t);
    }

    TabPage ControlsTab()
    {
        var outer = new TableLayoutPanel();
        outer.Dock = DockStyle.Fill;
        outer.RowCount = 2;
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));

        var g = new TableLayoutPanel();
        g.Dock = DockStyle.Fill;
        g.AutoScroll = true;
        g.ColumnCount = 5;
        g.Padding = new Padding(8);
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        foreach (string h in new[] { "Accion PS2", "Tecla 1", "Tecla 2", "Mando 1", "Mando 2" })
        {
            var l = new Label(); l.Text = h; l.Font = Theme.Section; l.ForeColor = Theme.Accent2; l.Tag = "header"; l.AutoSize = true;
            g.Controls.Add(l);
        }
        var choices = Bind.PadChoices();
        for (int a = 0; a < 24; a++)
        {
            var l = new Label(); l.Text = Bind.ActionNames[a]; l.AutoSize = true; l.Margin = new Padding(3, 7, 3, 3);
            g.Controls.Add(l);
            for (int s = 0; s < 2; s++)
            {
                var b = new Button(); b.Width = 94; b.Height = 26;
                int aa = a, ss = s;
                b.Click += delegate
                {
                    using (var k = new KeyCapture(Bind.ActionNames[aa]))
                    {
                        k.ShowDialog(this);
                        if (k.Result >= 0) { keys[aa, ss] = k.Result; keyButtons[aa, ss].Text = Bind.KeyName(k.Result); }
                    }
                };
                keyButtons[a, s] = b;
                g.Controls.Add(b);
            }
            for (int s = 0; s < 2; s++)
            {
                var c = new ComboBox(); c.DropDownStyle = ComboBoxStyle.DropDownList; c.Width = 168;
                c.DisplayMember = "Value"; c.ValueMember = "Key";
                c.DataSource = new List<KeyValuePair<int, string>>(choices);
                int aa = a, ss = s;
                c.SelectionChangeCommitted += delegate { pads[aa, ss] = (int)c.SelectedValue; };
                padBoxes[a, s] = c;
                g.Controls.Add(c);
            }
        }
        outer.Controls.Add(g, 0, 0);

        var t = Grid();
        t.ColumnStyles[0].Width = 200;
        cbGamepad = Combo("Automatico (el primer mando conectado)");
        Row(t, "Mando", cbGamepad, "Si el juego no usa tu mando, elige aqui cual usar. La lista son los mandos " +
                                   "que el juego vio la ultima vez que se abrio (conectalo y abre el juego una vez). " +
                                   "En el juego: F4 o LS + RS (los dos sticks hundidos) abre el menu.");
        cbGlyphs = Combo("Automatico (ultimo dispositivo usado)", "PlayStation", "Xbox (A B X Y)", "Teclado (teclas asignadas)");
        Row(t, "Simbolos de botones", cbGlyphs, "Los botones que muestran los menus y avisos del juego.");
        tbDead = Slider(0, 100);
        Row(t, "Zona muerta analogica", tbDead, "Como 'Analog Deadzone' de PCSX2.");
        tbScale = Slider(1, 200);
        Row(t, "Sensibilidad analogica", tbScale, "Como 'Analog Sensitivity' de PCSX2 (133 por defecto).");
        tbBtnDead = Slider(0, 100);
        Row(t, "Zona muerta de gatillos", tbBtnDead, null);
        string[] inv = { "No", "Izquierda/derecha", "Arriba/abajo", "Ambos" };
        cbInvL = Combo(inv); Row(t, "Invertir stick izquierdo", cbInvL, null);
        cbInvR = Combo(inv); Row(t, "Invertir stick derecho", cbInvR, null);
        ckVibe = Check("Vibracion"); Row(t, "", ckVibe, null);
        tbVibe = Slider(0, 100); Row(t, "Fuerza de vibracion", tbVibe, null);
        ckBlockMenu = Check("Bloquear el mando del juego con el menu F4 abierto"); Row(t, "", ckBlockMenu, null);
        outer.Controls.Add(t, 0, 1);
        return Page("Controles", outer);
    }

    TabPage AudioTab()
    {
        var t = Grid();
        tbVolume = Slider(0, 100);
        Row(t, "Volumen general", tbVolume, null);
        ckMute = Check("Silenciar cuando la ventana no esta activa");
        Row(t, "", ckMute, null);
        return Page("Audio", t);
    }

    readonly CheckedListBox modList = new CheckedListBox();
    readonly List<string> modManifests = new List<string>();
    static readonly Regex modEnabled = new Regex(
        @"^[ \t]*enabled[ \t]*=[ \t]*(true|false)(?=[ \t]*(?:#|\r?$))",
        RegexOptions.Multiline);
    static readonly Regex modSection = new Regex(@"^[ \t]*\[", RegexOptions.Multiline);

    static int ManifestHeaderEnd(string manifest)
    {
        var section = modSection.Match(manifest);
        return section.Success ? section.Index : manifest.Length;
    }

    TabPage ModsTab()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label {
            Text = "Marca los mods que quieras usar. Los cambios se aplican al iniciar el juego.",
            AutoSize = true
        }, 0, 0);
        modList.Dock = DockStyle.Fill;
        modList.CheckOnClick = true;
        panel.Controls.Add(modList, 0, 1);
        if (Directory.Exists(modsDir))
        {
            foreach (string dir in Directory.GetDirectories(modsDir).OrderBy(d => d))
            {
                string id = Path.GetFileName(dir);
                if (id.StartsWith(".") || id.Equals("cheats", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("municion", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("guiado_misiles", StringComparison.OrdinalIgnoreCase)   // "Municion y dinero" page
                    || id.Equals("espanol", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("es419_prueba", StringComparison.OrdinalIgnoreCase))
                    continue;
                string path = Path.Combine(dir, "mod.toml");
                string manifest = File.Exists(path) ? File.ReadAllText(path) : "";
                string header = manifest.Substring(0, ManifestHeaderEnd(manifest));
                var name = Regex.Match(header, "^\\s*name[ \\t]*=[ \\t]*\"([^\"]+)\"", RegexOptions.Multiline);
                var enabled = modEnabled.Match(header);
                modManifests.Add(path);
                modList.Items.Add(name.Success ? name.Groups[1].Value : id,
                    !enabled.Success || enabled.Groups[1].Value == "true");
            }
        }
        if (modManifests.Count == 0)
            panel.Controls.Add(new Label { Text = "No hay mods instalados.", AutoSize = true }, 0, 1);
        return Page("Mods", panel);
    }

    void SaveMods()
    {
        for (int i = 0; i < modManifests.Count; i++)
        {
            string path = modManifests[i];
            string manifest = File.Exists(path) ? File.ReadAllText(path) : "";
            int end = ManifestHeaderEnd(manifest);
            var setting = modEnabled.Match(manifest.Substring(0, end));
            bool enabled = modList.GetItemChecked(i);
            if (enabled == (!setting.Success || setting.Groups[1].Value == "true"))
                continue;
            string value = enabled ? "true" : "false";
            if (setting.Success)
            {
                var previous = setting.Groups[1];
                manifest = manifest.Remove(previous.Index, previous.Length).Insert(previous.Index, value);
            }
            else
                manifest = "enabled = " + value + Environment.NewLine + manifest;
            string temp = path + ".launcher.tmp";
            File.WriteAllText(temp, manifest, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
    }

    readonly string[] ammoKeys = { "gun", "missiles", "special" };
    readonly TrackBar[] ammoSliders = new TrackBar[3];
    string AmmoManifest { get { return Path.Combine(modsDir, "municion", "mod.toml"); } }

    readonly NumericUpDown credits = new NumericUpDown {
        Maximum = 99999999, Increment = 10000, ThousandsSeparator = true, Width = 160
    };

    // Master switch of the "Municion y dinero" page: turns the ammo/credits mod
    // (mods\municion) and the missile guidance mod (mods\guiado_misiles) on or
    // off together.  The values are kept when it is off.
    readonly CheckBox ammoMaster = new CheckBox {
        Text = "Activar las funciones de esta pagina (municion extra, creditos y guiado de misiles)",
        AutoSize = true, Margin = new Padding(3, 2, 3, 10) };
    readonly List<Control> ammoControls = new List<Control>();

    static bool ManifestEnabled(string path)
    {
        if (!File.Exists(path)) return false;
        return new Ini(path).Get("", "enabled", "true").Trim().Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    bool AmmoInstalled { get { return File.Exists(Path.Combine(modsDir, "municion", "script.lua")); } }

    void ApplyAmmoMaster()
    {
        foreach (var c in ammoControls) c.Enabled = ammoMaster.Checked && AmmoInstalled;
        // the guidance slider also needs its mod
        foreach (var s in guidanceSliders) if (s != null) s.Enabled = ammoMaster.Checked && GuidanceInstalled;
    }

    TabPage AmmoTab()
    {
        var panel = Grid();
        ammoMaster.Checked = ManifestEnabled(AmmoManifest) || ManifestEnabled(Path.Combine(GuidanceDir, "mod.toml"));
        ammoMaster.Enabled = AmmoInstalled || GuidanceInstalled;
        ammoMaster.CheckedChanged += delegate { ApplyAmmoMaster(); };
        panel.Controls.Add(ammoMaster);
        panel.SetColumnSpan(ammoMaster, 2);
        var help = new Label { Text = "0% = original, +100% = el doble. Las bombas (UGB, FAEB...) son el arma especial " +
                                      "del avion. Topes: canon +500%, misiles +300%, arma especial +800% (el juego no pasa de 9999 balas, " +
                                      "255 misiles ni 99 armas especiales). Se aplica al iniciar el juego y se ve tanto en el hangar como en la mision.",
                               AutoSize = true, MaximumSize = new Size(560, 0) };
        panel.Controls.Add(help);
        panel.SetColumnSpan(help, 2);
        string[] labels = { "Canon", "Misiles comunes", "Arma especial (bombas, QAAM...)" };
        var manifest = new Ini(AmmoManifest);
        for (int i = 0; i < ammoSliders.Length; i++)
        {
            // cannon +500%, missiles +300%, special weapon (bombs, QAAM...) +800%
            int max = ammoKeys[i] == "special" ? 800 : ammoKeys[i] == "missiles" ? 300 : 500;
            var slider = new TrackBar { Minimum = 0, Maximum = max, TickFrequency = max / 10, LargeChange = 25, SmallChange = 5 };
            var value = new Label { AutoSize = true };
            slider.ValueChanged += delegate { value.Text = "+" + slider.Value + "%"; };
            slider.Value = Math.Max(0, Math.Min(max, manifest.GetInt("config", ammoKeys[i], 0)));
            value.Text = "+" + slider.Value + "%";
            var controls = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
            slider.Width = 250;
            controls.Controls.Add(slider);
            controls.Controls.Add(value);
            ammoSliders[i] = slider;
            Row(panel, labels[i], controls, null);
            ammoControls.Add(controls);
        }
        var reset = new Button { Text = "Restablecer municion original", AutoSize = true };
        reset.Click += delegate { foreach (var slider in ammoSliders) slider.Value = 0; };
        panel.Controls.Add(reset);
        panel.SetColumnSpan(reset, 2);
        ammoControls.Add(reset);
        credits.Value = Math.Max(0, Math.Min(99999999, manifest.GetInt("config", "credits", 0)));
        Row(panel, "Creditos minimos (0 = desactivado)", credits,
            "Mientras el truco este activo, el saldo se repone hasta este importe.");
        ammoControls.Add(credits);
        var guidance = GuidancePanel();
        panel.Controls.Add(guidance);
        panel.SetColumnSpan(guidance, 2);
        ApplyAmmoMaster();
        return Page("Municion y dinero", panel);
    }

    void SaveAmmo()
    {
        string path = AmmoManifest;
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(path), "script.lua"))) return;
        bool enabled = ammoMaster.Checked && (credits.Value > 0 || ammoSliders.Any(slider => slider.Value != 0));
        var lines = new List<string> {
            "name = \"Municion extra (launcher)\"", "game = \"SLUS-20152\"",
            "enabled = " + (enabled ? "true" : "false"), "", "[config]"
        };
        for (int i = 0; i < ammoSliders.Length; i++)
            lines.Add(ammoKeys[i] + " = " + ammoSliders[i].Value.ToString(CultureInfo.InvariantCulture));
        lines.Add("credits = " + credits.Value.ToString(CultureInfo.InvariantCulture));
        string temp = path + ".tmp";
        File.WriteAllLines(temp, lines, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    TabPage CheatsTab()
    {
        var split = new SplitContainer();
        split.Dock = DockStyle.Fill;
        split.FixedPanel = FixedPanel.Panel1;
        split.SplitterWidth = 6;
        bool placed = false;
        split.SizeChanged += delegate
        {
            if (placed || split.Width < 600) return;
            placed = true;
            split.SplitterDistance = 300;
        };

        var left = new TableLayoutPanel();
        left.Dock = DockStyle.Fill;
        left.RowCount = 3;
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var info = new Label();
        info.Text = "Marca los cheats que quieres activos. Se aplican al iniciar la proxima partida.";
        info.AutoSize = true;
        info.MaximumSize = new Size(270, 0);
        left.Controls.Add(info, 0, 0);
        clCheats = new CheckedListBox();
        clCheats.Dock = DockStyle.Fill;
        clCheats.CheckOnClick = true;
        clCheats.ItemCheck += (s, e) => { if (e.Index >= 0 && e.Index < cheats.Count) cheats[e.Index].Enabled = e.NewValue == CheckState.Checked; };
        clCheats.SelectedIndexChanged += delegate { ShowCheat(); };
        left.Controls.Add(clCheats, 0, 1);
        var buttons = new FlowLayoutPanel();
        buttons.AutoSize = true;
        buttons.Dock = DockStyle.Fill;
        var bAdd = new Button(); bAdd.Text = "Nuevo"; bAdd.Click += delegate { AddCheat(); };
        var bDel = new Button(); bDel.Text = "Eliminar"; bDel.Click += delegate { DeleteCheat(); };
        var bImp = new Button(); bImp.Text = "Importar .pnach"; bImp.Width = 120; bImp.Click += delegate { ImportPnach(); };
        buttons.Controls.AddRange(new Control[] { bAdd, bDel, bImp });
        left.Controls.Add(buttons, 0, 2);
        split.Panel1.Controls.Add(left);

        var right = new TableLayoutPanel();
        right.Dock = DockStyle.Fill;
        right.RowCount = 3;
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var help = new Label();
        help.Text = "Codigos del cheat seleccionado. Acepta lineas pnach de PCSX2 (patch=1,EE,00XXXXXX,word,VVVVVVVV) " +
                    "o codigos crudos sin cifrar de 8+8 cifras (0 = byte, 1 = 16 bits, 2 = 32 bits). " +
                    "Solo funcionan los que cambian datos (municion, dinero, vida...): el codigo del juego esta " +
                    "recompilado, asi que los que parchean instrucciones se ignoran.";
        help.AutoSize = true;
        help.MaximumSize = new Size(440, 0);
        right.Controls.Add(help, 0, 0);
        tbCheatCodes = new TextBox();
        tbCheatCodes.Multiline = true;
        tbCheatCodes.ScrollBars = ScrollBars.Vertical;
        tbCheatCodes.Font = new Font("Consolas", 9.5f);
        tbCheatCodes.Dock = DockStyle.Fill;
        right.Controls.Add(tbCheatCodes, 0, 1);
        var bApply = new Button(); bApply.Text = "Aplicar codigos al cheat"; bApply.Width = 180;
        bApply.Click += delegate { ApplyCodes(); };
        right.Controls.Add(bApply, 0, 2);
        split.Panel2.Controls.Add(right);
        return Page("Cheats", split);
    }

    // ------------------------------------------------------ missile guidance
    // mods\guiado_misiles: turn_multiplier steers every guided missile (common
    // and special).  The slider shows the bonus over the original game:
    // +0% .. +100% (x1.0 .. x2.0).
    readonly TrackBar[] guidanceSliders = new TrackBar[1];
    readonly string[] guidanceKeys = { "turn_multiplier" };
    string GuidanceDir { get { return Path.Combine(modsDir, "guiado_misiles"); } }
    bool GuidanceInstalled { get { return File.Exists(Path.Combine(GuidanceDir, "script.lua")); } }

    static float ManifestFloat(Ini manifest, string key, float def)
    {
        float v;
        string s = manifest.Get("config", key, "").Trim().Trim('"');
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
    }

    Control GuidancePanel()
    {
        var box = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(3, 10, 3, 3) };
        box.Controls.Add(new Label { Text = "Guiado de misiles", AutoSize = true, Font = Theme.Section, ForeColor = Theme.Accent2, Tag = "header" });
        bool installed = GuidanceInstalled;
        var manifest = new Ini(Path.Combine(GuidanceDir, "mod.toml"));
        string[] labels = { "Todos los misiles (comunes y especiales)" };
        for (int i = 0; i < guidanceSliders.Length; i++)
        {
            int pct = (int)Math.Round((ManifestFloat(manifest, guidanceKeys[i], 1.75f) - 1f) * 100f);
            var slider = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, LargeChange = 10, Width = 200,
                                        AutoSize = false, Height = 32 };
            var value = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
            slider.ValueChanged += delegate { value.Text = "+" + slider.Value + "%"; };
            slider.Value = Clamp(pct, 0, 100);
            value.Text = "+" + slider.Value + "%";
            slider.Enabled = installed;
            guidanceSliders[i] = slider;
            box.Controls.Add(new Label { Text = labels[i], AutoSize = true, Margin = new Padding(3, 6, 3, 0) });
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            row.Controls.Add(slider);
            row.Controls.Add(value);
            box.Controls.Add(row);
        }
        box.Controls.Add(new Label {
            Text = installed ? "0% = guiado original. Se aplica al iniciar el juego."
                             : "Desactivado: falta el mod guiado_misiles en la carpeta de mods.",
            AutoSize = true, MaximumSize = new Size(270, 0), Tag = "muted", ForeColor = Theme.Muted });
        return box;
    }

    void SaveGuidance()
    {
        if (!GuidanceInstalled || guidanceSliders[0] == null) return;
        string path = Path.Combine(GuidanceDir, "mod.toml");
        var manifest = new Ini(path);
        // on/off follows the master switch of the "Municion y dinero" page
        string enabled = ammoMaster.Checked ? "true" : "false";
        float range = ManifestFloat(manifest, "standard_lock_range_multiplier", 1.69f);
        float turn = 1f + guidanceSliders[0].Value / 100f;
        var lines = new List<string> {
            "name = \"Guiado de misiles\"", "version = \"1.2\"", "game = \"SLUS-20152\"", "enabled = " + enabled, "",
            "[config]",
            "# multiplicador del giro de guiado de todos los misiles: 1.00 = original, 2.00 = el doble",
            "turn_multiplier = \"" + turn.ToString("0.00", CultureInfo.InvariantCulture) + "\"",
            "standard_lock_range_multiplier = \"" + range.ToString("0.00", CultureInfo.InvariantCulture) + "\""
        };
        string temp = path + ".tmp";
        File.WriteAllLines(temp, lines, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    // ---------------------------------------------------------------- cheats
    void RefreshCheats(int select)
    {
        clCheats.Items.Clear();
        foreach (var c in cheats) clCheats.Items.Add(c.Name + "  (" + c.Patches.Count + ")", c.Enabled);
        if (select >= 0 && select < cheats.Count) clCheats.SelectedIndex = select;
        ShowCheat();
    }

    void ShowCheat()
    {
        int i = clCheats.SelectedIndex;
        tbCheatCodes.Text = i >= 0 && i < cheats.Count
            ? string.Join("\r\n", cheats[i].Patches.Select(p => "patch=" + p).ToArray()) : "";
    }

    void AddCheat()
    {
        string name = Prompt("Nombre del cheat", "Nuevo cheat");
        if (string.IsNullOrEmpty(name)) return;
        var c = new Cheat(); c.Name = name.Replace("[", "(").Replace("]", ")");
        cheats.Add(c);
        RefreshCheats(cheats.Count - 1);
        tbCheatCodes.Focus();
    }

    void DeleteCheat()
    {
        int i = clCheats.SelectedIndex;
        if (i < 0) return;
        if (MessageBox.Show(this, "Eliminar \"" + cheats[i].Name + "\"?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        cheats.RemoveAt(i);
        RefreshCheats(Math.Min(i, cheats.Count - 1));
    }

    bool ApplyCodes()
    {
        int i = clCheats.SelectedIndex;
        if (i < 0) return true;
        var errors = new List<string>();
        var p = Cheats.Parse(tbCheatCodes.Text, errors);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, "No se aplicaron los codigos:\n\n" + string.Join("\n", errors.Take(10).ToArray()), Text);
            return false;
        }
        cheats[i].Patches = p;
        RefreshCheats(i);
        return true;
    }

    void ImportPnach()
    {
        using (var d = new OpenFileDialog())
        {
            d.Filter = "Parches PCSX2 (*.pnach)|*.pnach|Todos los archivos|*.*";
            if (d.ShowDialog(this) != DialogResult.OK) return;
            Cheat cur = null;
            int added = 0;
            foreach (string raw in File.ReadAllLines(d.FileName))
            {
                string l = raw.Trim();
                if (l.StartsWith("[") && l.EndsWith("]")) { cur = new Cheat(); cur.Name = l.Substring(1, l.Length - 2); cheats.Add(cur); added++; continue; }
                if (l.StartsWith("//") && l.Length > 2 && cur == null) continue;
                if (!l.StartsWith("patch=")) continue;
                if (cur == null) { cur = new Cheat(); cur.Name = Path.GetFileNameWithoutExtension(d.FileName); cheats.Add(cur); added++; }
                cur.Patches.Add(l.Substring(6).Trim());
            }
            cheats.RemoveAll(c => c.Patches.Count == 0);
            RefreshCheats(cheats.Count - 1);
            MessageBox.Show(this, "Importados: " + added + " cheat(s). Activalos marcandolos en la lista.", Text);
        }
    }

    string Prompt(string title, string def)
    {
        using (var f = new Form())
        {
            f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(360, 90); f.MaximizeBox = f.MinimizeBox = false;
            var tb = new TextBox(); tb.Text = def; tb.SetBounds(12, 14, 336, 24);
            var ok = new Button(); ok.Text = "Aceptar"; ok.DialogResult = DialogResult.OK; ok.SetBounds(192, 52, 75, 26);
            var no = new Button(); no.Text = "Cancelar"; no.DialogResult = DialogResult.Cancel; no.SetBounds(273, 52, 75, 26);
            f.Controls.AddRange(new Control[] { tb, ok, no });
            f.AcceptButton = ok; f.CancelButton = no;
            Theme.StyleDialog(f);
            return f.ShowDialog(this) == DialogResult.OK ? tb.Text.Trim() : null;
        }
    }

    // ---------------------------------------------------------- load / save
    void LoadAll()
    {
        ini = new Ini(settingsPath);
        if (File.Exists(DiscPathFile)) discPath.Text = File.ReadAllText(DiscPathFile).Trim();
        LoadSavesDir();
        LoadLanguage();
        cbRes.SelectedIndex = Clamp(ini.GetInt("graphics", "internal_resolution", 3), 0, 8);
        cbTexFilter.SelectedIndex = Clamp(ini.GetInt("graphics", "texture_filter", 0), 0, 2);
        int an = ini.GetInt("graphics", "anisotropy", 16);
        cbAniso.SelectedIndex = an >= 16 ? 4 : an >= 8 ? 3 : an >= 4 ? 2 : an >= 2 ? 1 : 0;
        cbScale.SelectedIndex = Clamp(ini.GetInt("graphics", "scale_filter", 3), 0, 3);
        ckUiSmooth.Checked = ini.GetInt("graphics", "ui_smooth", 1) != 0;
        tbRefl.Value = Clamp(ini.GetInt("graphics", "reflection_strength", 60), 0, 100);
        ckHd.Checked = ini.GetInt("graphics", "hd_textures", 1) != 0;
        ckDeint.Checked = ini.GetInt("graphics", "deinterlace", 1) != 0;
        ckFxaa.Checked = ini.GetInt("postfx", "fxaa", 1) != 0;
        tbSharpen.Value = Clamp((int)Math.Round(ini.GetFloat("postfx", "sharpen", 0.45f) * 100), 0, 100);
        tbBright.Value = Clamp((int)Math.Round(ini.GetFloat("postfx", "brightness", 0f) * 100), -50, 50);
        tbContrast.Value = Clamp((int)Math.Round(ini.GetFloat("postfx", "contrast", 1f) * 100), 50, 150);
        tbGamma.Value = Clamp((int)Math.Round(ini.GetFloat("postfx", "gamma", 1f) * 100), 50, 200);
        tbSat.Value = Clamp((int)Math.Round(ini.GetFloat("postfx", "saturation", 1f) * 100), 0, 200);

        Size native = Display.Native();
        cbWindow.SelectedIndex = Clamp(ini.GetInt("display", "window_mode", 1), 0, 2);
        nuW.Value = Clamp(ini.GetInt("display", "window_width", native.Width), 320, 16384);
        nuH.Value = Clamp(ini.GetInt("display", "window_height", native.Height), 224, 16384);
        cbAspect.SelectedIndex = Clamp(ini.GetInt("display", "aspect", 3), 0, 4);
        ckWide.Checked = ini.GetInt("display", "widescreen", 1) != 0;
        cbHud.SelectedIndex = Clamp(ini.GetInt("display", "hud_layout", 0), 0, 2);
        ckInteger.Checked = ini.GetInt("display", "integer_scale", 0) != 0;
        cbPresent.SelectedIndex = Clamp(ini.GetInt("display", "present_mode", 1), 0, 3);
        ckSync.Checked = ini.GetInt("display", "sync_to_display", 1) != 0;
        nuFps.Value = Clamp(ini.GetInt("display", "fps_limit", 0), 0, 240);
        ckShowFps.Checked = ini.GetInt("display", "show_fps", 1) != 0;
        ckHideCursor.Checked = ini.GetInt("display", "hide_cursor_fullscreen", 1) != 0;

        // A file with no controls yet (a new installation): the game's defaults.
        // (Loading "0 0" for them and saving it left every control unbound.)
        bool anyKey = false, anyPad = false;
        for (int a = 0; a < 24; a++)
        {
            string[] k = ini.Get("keyboard", Bind.Actions[a], "0 0").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string[] p = ini.Get("controller", Bind.Actions[a], "0 0").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int s = 0; s < 2; s++)
            {
                int v;
                keys[a, s] = s < k.Length && int.TryParse(k[s], out v) ? v : 0;
                pads[a, s] = s < p.Length && int.TryParse(p[s], out v) ? v : 0;
                anyKey |= keys[a, s] != 0;
                anyPad |= pads[a, s] != 0;
            }
        }
        for (int a = 0; a < 24; a++)
            for (int s = 0; s < 2; s++)
            {
                if (!anyKey) keys[a, s] = Bind.DefaultKeys[a, s];
                if (!anyPad) pads[a, s] = Bind.DefaultPads[a, s];
                keyButtons[a, s].Text = Bind.KeyName(keys[a, s]);
                SelectPad(padBoxes[a, s], pads[a, s]);
            }
        LoadGamepadChoice();
        tbDead.Value = Clamp((int)Math.Round(ini.GetFloat("analog", "deadzone", 0f) * 100), 0, 100);
        tbScale.Value = Clamp((int)Math.Round(ini.GetFloat("analog", "axis_scale", 1.33f) * 100), 1, 200);
        tbBtnDead.Value = Clamp((int)Math.Round(ini.GetFloat("analog", "button_deadzone", 0f) * 100), 0, 100);
        cbInvL.SelectedIndex = Clamp(ini.GetInt("analog", "invert_left", 0), 0, 3);
        cbInvR.SelectedIndex = Clamp(ini.GetInt("analog", "invert_right", 0), 0, 3);
        ckVibe.Checked = ini.GetInt("input", "vibration", 1) != 0;
        cbGlyphs.SelectedIndex = Clamp(ini.GetInt("input", "button_glyphs", 0), 0, 3);
        tbVibe.Value = Clamp((int)Math.Round(ini.GetFloat("input", "vibration_strength", 0.65f) * 100), 0, 100);
        ckBlockMenu.Checked = ini.GetInt("input", "block_game_input_in_menu", 1) != 0;

        tbVolume.Value = Clamp((int)Math.Round(ini.GetFloat("audio", "volume", 1f) * 100), 0, 100);
        ckMute.Checked = ini.GetInt("audio", "mute_in_background", 0) != 0;

        cheats = Cheats.Load(cheatsPath);
        RefreshCheats(cheats.Count > 0 ? 0 : -1);
    }

    void SelectPad(ComboBox c, int code)
    {
        // pads that are not built yet (handle created lazily) still take the value
        if (!c.IsHandleCreated) c.CreateControl();
        c.SelectedValue = code;
        if (c.SelectedIndex < 0 && c.Items.Count > 0) c.SelectedIndex = 0;
    }

    bool SaveAll()
    {
        if (clCheats.SelectedIndex >= 0 && !ApplyCodes()) return false;
        try
        {
            ini.SetInt("graphics", "preset", 4);
            ini.SetInt("graphics", "internal_resolution", cbRes.SelectedIndex);
            ini.SetInt("graphics", "texture_filter", cbTexFilter.SelectedIndex);
            ini.SetInt("graphics", "anisotropy", new[] { 0, 2, 4, 8, 16 }[cbAniso.SelectedIndex]);
            ini.SetInt("graphics", "scale_filter", cbScale.SelectedIndex);
            ini.SetInt("graphics", "ui_smooth", ckUiSmooth.Checked ? 1 : 0);
            ini.SetInt("graphics", "reflection_strength", tbRefl.Value);
            ini.SetInt("graphics", "hd_textures", ckHd.Checked ? 1 : 0);
            ini.SetInt("graphics", "deinterlace", ckDeint.Checked ? 1 : 0);
            ini.SetInt("postfx", "fxaa", ckFxaa.Checked ? 1 : 0);
            ini.SetFloat("postfx", "sharpen", tbSharpen.Value / 100f);
            ini.SetFloat("postfx", "brightness", tbBright.Value / 100f);
            ini.SetFloat("postfx", "contrast", tbContrast.Value / 100f);
            ini.SetFloat("postfx", "gamma", tbGamma.Value / 100f);
            ini.SetFloat("postfx", "saturation", tbSat.Value / 100f);

            ini.SetInt("display", "window_mode", cbWindow.SelectedIndex);
            if (cbWindow.SelectedIndex > 0) ini.SetInt("display", "fullscreen_type", cbWindow.SelectedIndex);
            ini.SetInt("display", "window_width", (int)nuW.Value);
            ini.SetInt("display", "window_height", (int)nuH.Value);
            ini.SetInt("display", "aspect", cbAspect.SelectedIndex);
            ini.SetInt("display", "widescreen", ckWide.Checked ? 1 : 0);
            ini.SetInt("display", "hud_layout", cbHud.SelectedIndex);
            ini.SetInt("display", "integer_scale", ckInteger.Checked ? 1 : 0);
            ini.SetInt("display", "present_mode", cbPresent.SelectedIndex);
            ini.Set("display", "sync_to_display", ckSync.Checked ? "1" : "0",
                    "1 lock the game's 59.94-Hz clock to a 60/120-Hz monitor's vblanks (even frame pacing)");
            ini.SetInt("display", "fps_limit", (int)nuFps.Value);
            ini.SetInt("display", "show_fps", ckShowFps.Checked ? 1 : 0);
            ini.SetInt("display", "hide_cursor_fullscreen", ckHideCursor.Checked ? 1 : 0);

            for (int a = 0; a < 24; a++)
            {
                ini.Set("keyboard", Bind.Actions[a], string.Format("{0,3} {1,3}", keys[a, 0], keys[a, 1]),
                        Bind.KeyName(keys[a, 0]) + ", " + Bind.KeyName(keys[a, 1]));
                ini.Set("controller", Bind.Actions[a], string.Format("{0,3} {1,3}", pads[a, 0], pads[a, 1]),
                        Bind.PadName(pads[a, 0]) + ", " + Bind.PadName(pads[a, 1]));
            }
            ini.SetFloat("analog", "deadzone", tbDead.Value / 100f);
            ini.SetFloat("analog", "axis_scale", tbScale.Value / 100f);
            ini.SetFloat("analog", "button_deadzone", tbBtnDead.Value / 100f);
            ini.SetInt("analog", "invert_left", cbInvL.SelectedIndex);
            ini.SetInt("analog", "invert_right", cbInvR.SelectedIndex);
            ini.SetInt("input", "vibration", ckVibe.Checked ? 1 : 0);
            ini.SetInt("input", "button_glyphs", cbGlyphs.SelectedIndex);
            ini.Set("input", "gamepad", (cbGamepad.SelectedIndex > 0 && cbGamepad.SelectedIndex - 1 < gamepadIds.Count
                                         ? gamepadIds[cbGamepad.SelectedIndex - 1] : 0).ToString(CultureInfo.InvariantCulture),
                    "0 automatic (the first controller), else the id of the chosen one (ac4_gamepads.txt)");
            ini.SetFloat("input", "vibration_strength", tbVibe.Value / 100f);
            ini.SetInt("input", "block_game_input_in_menu", ckBlockMenu.Checked ? 1 : 0);
            ini.SetFloat("audio", "volume", tbVolume.Value / 100f);
            ini.SetInt("audio", "mute_in_background", ckMute.Checked ? 1 : 0);
            ini.Save();

            Cheats.Save(cheatsPath, cheats);
            Cheats.WriteMod(modsDir, cheats);
            SaveMods();
            SaveAmmo();
            SaveGuidance();
            SaveLanguage();
            if (discPath.Text.Length > 0) File.WriteAllText(DiscPathFile, discPath.Text);
            else File.Delete(DiscPathFile);
            StoreSavesDir();
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "No se pudo guardar la configuracion:\n\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    // Test hook: load everything into the controls and save it back unchanged.
    public bool RoundTrip() { CreateControl(); return SaveAll(); }

    void Defaults()
    {
        if (MessageBox.Show(this, "Volver a los valores por defecto de graficos, pantalla, audio y analogico?\n" +
                            "(Las funciones de \"Municion y dinero\" se desactivan; las teclas, el mando y los cheats no cambian.)",
                            Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        ammoMaster.Checked = false;   // ammo, credits and guidance are off by default
        cbRes.SelectedIndex = 3; cbTexFilter.SelectedIndex = 0; cbAniso.SelectedIndex = 4; cbScale.SelectedIndex = 3;
        ckUiSmooth.Checked = true; ckHd.Checked = true; ckDeint.Checked = true; ckFxaa.Checked = true;
        tbSharpen.Value = 45; tbRefl.Value = 60; tbBright.Value = 0; tbContrast.Value = 100; tbGamma.Value = 100; tbSat.Value = 100;
        // pantalla: borderless at the monitor's resolution, 16:9, as Display.Apply
        Size native = Display.Native();
        cbWindow.SelectedIndex = 1; nuW.Value = Clamp(native.Width, 320, 16384); nuH.Value = Clamp(native.Height, 224, 16384);
        cbAspect.SelectedIndex = 3; ckWide.Checked = true; cbHud.SelectedIndex = 0; ckHideCursor.Checked = true;
        ckInteger.Checked = false; cbPresent.SelectedIndex = 1; ckSync.Checked = true; nuFps.Value = 0; ckShowFps.Checked = true;
        tbDead.Value = 0; tbScale.Value = 133; tbBtnDead.Value = 0; cbInvL.SelectedIndex = 0; cbInvR.SelectedIndex = 0;
        ckVibe.Checked = true; tbVibe.Value = 65; ckBlockMenu.Checked = true; tbVolume.Value = 100; ckMute.Checked = false;
    }

    // ------------------------------------------------------------------ play
    string FindDisc()
    {
        string chosen = discPath.Text.Trim();
        if (chosen.Length > 0 && (File.Exists(chosen) || Directory.Exists(chosen))) return chosen;
        // the disc the installer extracted: the game needs no ISO then
        string extracted = Path.Combine(root, "gamedata", "disc");
        if (File.Exists(Path.Combine(extracted, "SLUS_201.52"))) return extracted;
        if (chosen.Length > 0) return chosen;
        string game = Path.GetDirectoryName(runtime);
        var parent = new DirectoryInfo(root).Parent;
        foreach (string dir in new[] { root, game, parent == null ? null : parent.FullName })
        {
            if (dir == null || !Directory.Exists(dir)) continue;
            foreach (string f in Directory.GetFiles(dir, "*.iso"))
                return f;
        }
        return null;
    }

    public static bool ForceBuild;

    public void Play()
    {
        string exe = Path.Combine(runtime, "ac4.exe");
        // compile first only when the port's source is here and ac4.exe is
        // missing or out of date; this shows its own progress window
        Builder.Init(root);
        Hide();
        if (!Builder.EnsureReady(ForceBuild, true)) { Close(); return; }
        string disc = FindDisc();
        if (!File.Exists(exe)) { Show(); MessageBox.Show(this, "No encuentro " + exe, Text); return; }
        if (disc == null || !(File.Exists(disc) || Directory.Exists(disc)))
        {
            Show();
            MessageBox.Show(this, "No encuentro la ISO. Selecciona el archivo desde la pestana Juego.", Text);
            return;
        }
        string game = Path.GetDirectoryName(runtime);
        if (savePath.Text.Length == 0) LoadSavesDir();
        string saves = SavesDir();
        try { Directory.CreateDirectory(saves); }
        catch
        {
            // the chosen folder is gone (unplugged drive...): fall back, and say so
            MessageBox.Show(this, "No se puede usar la carpeta de partidas:\n" + saves +
                            "\n\nSe usará la carpeta predeterminada.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            saves = DefaultSavesDir;
            Directory.CreateDirectory(saves);
        }
        Directory.CreateDirectory(modsDir);
        string log = Path.Combine(game, "last-play.log");
        string csv = Path.Combine(game, "last-play.csv");
        bool diagnostic = Environment.GetEnvironmentVariable("AC4_DIAGNOSTIC") == "1";

        var env = new Dictionary<string, string>();
        env["PS2_SHADER_DIR"] = Path.Combine(runtime, "shaders");
        // the memory image the installer made from the ISO: <install>\gamedata
        string gameImage = Path.GetFullPath(Path.Combine(runtime, @"..\..\gamedata\ps2_image.bin"));
        if (File.Exists(gameImage)) env["PS2_IMAGE"] = gameImage;
        env["PS2_TEXTURE_PACK"] = Path.Combine(runtime, "textures", "hd");
        env["PS2_SAVE_DIR"] = saves;
        env["PS2_MOD_DIR"] = modsDir;
        /* Mission 5 can briefly make the graphics worker fall behind.  Three
           queued fields and GPU frames absorb those peaks without reducing the
           internal resolution, texture quality, or post-processing. */
        env["PS2_ASYNC_GFX"] = "1"; env["PS2_ASYNC_FIELDS"] = "3"; env["SDL_JOYSTICK_THREAD"] = "1";
        env["PS2_LATE_INPUT"] = "1"; env["PS2_GPU_FRAMES"] = "3"; env["PS2_LOCKSTEP"] = "0";
        env["DISABLE_RTSS_LAYER"] = "1"; env["DISABLE_LAYER_NV_PRESENT_1"] = "1";
        /* Per-frame GPU timestamps and CSV output are useful for diagnosis, but
           add CPU work and sustained disk writes during every normal game. */
        if (diagnostic) { env["PS2_SPEED_LOG"] = "1"; env["PS2_FRAME_TIMINGS"] = csv; }

        Hide();
        // 1. shader/pipeline optimisation (instant when nothing changed)
        Run(exe, "--precompile", env, null);
        // 2. the game, its output kept as the session log
        int rc = Run(exe, "--data \"" + runtime + "\" --disc \"" + disc + "\" --vblanks 0 --watchdog 0", env, log);
        WarnAboutGpu(log);
        try
        {
            Directory.CreateDirectory(logsDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (File.Exists(log)) File.Copy(log, Path.Combine(logsDir, "play-" + stamp + ".log"), true);
            if (diagnostic && File.Exists(csv)) File.Copy(csv, Path.Combine(logsDir, "play-" + stamp + ".csv"), true);
            foreach (var old in new DirectoryInfo(logsDir).GetFiles("play-*.log").OrderByDescending(f => f.LastWriteTime).Skip(15))
            {
                old.Delete();
                string c = Path.ChangeExtension(old.FullName, ".csv");
                if (File.Exists(c)) File.Delete(c);
            }
        }
        catch { }
        Close();
    }

    // A PC with both an integrated GPU (e.g. Ryzen 4650G) and a graphics card
    // may run the game on the integrated one -- typically when the monitor is
    // plugged into the motherboard.  That is many times slower; say so.
    static void WarnAboutGpu(string log)
    {
        try
        {
            string used = null, unusable = null;
            bool usedDiscrete = false, haveDiscrete = false;
            var list = new Regex(@"^vk: GPU \d+: (.+?) \(API [\d.]+, (discrete|not discrete)\)(?: -- unusable: (.*))?$");
            var chosen = new Regex(@"^vk: (.+?) \(API \d+\.\d+\.\d+\)$");
            var discrete = new HashSet<string>();
            foreach (string line in File.ReadLines(log).Take(400))
            {
                var m = list.Match(line);
                if (m.Success)
                {
                    if (m.Groups[2].Value == "discrete")
                    {
                        haveDiscrete = true;
                        if (m.Groups[3].Success) unusable = m.Groups[1].Value + ": " + m.Groups[3].Value;
                        else discrete.Add(m.Groups[1].Value);
                    }
                    continue;
                }
                var c = chosen.Match(line);
                if (c.Success && used == null) { used = c.Groups[1].Value; usedDiscrete = discrete.Contains(used); }
            }
            if (used == null || usedDiscrete || !haveDiscrete) return;
            string marker = Path.Combine(Path.GetDirectoryName(log), "gpu-warning.txt");
            string text = "El juego se ejecutó en la gráfica integrada (" + used + "), no en la tarjeta gráfica" +
                          (unusable != null ? " (" + unusable + ")" : "") + ".\n\n" +
                          "Esto baja mucho el rendimiento. Conecta el monitor a la salida de la tarjeta gráfica " +
                          "(no a la de la placa base) y actualiza su driver.";
            if (File.Exists(marker) && File.ReadAllText(marker) == text) return;   // said once
            File.WriteAllText(marker, text);
            MessageBox.Show(text, "Ace Combat 04", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    static int Run(string exe, string args, Dictionary<string, string> env, string logPath)
    {
        // let the game's window take the foreground (keyboard focus), also
        // after the compile window of the first run has closed
        try { AllowSetForegroundWindow(-1 /* ASFW_ANY */); } catch { }
        var psi = new ProcessStartInfo(exe, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = Path.GetDirectoryName(exe);
        foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
        StreamWriter w = null;
        if (logPath != null)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            w = new StreamWriter(logPath, false, new UTF8Encoding(false));
            w.AutoFlush = true;
        }
        try
        {
            using (var p = Process.Start(psi))
            {
                if (w != null)
                {
                    object gate = new object();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (gate) w.WriteLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (gate) w.WriteLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                }
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        finally { if (w != null) w.Dispose(); }
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string root = AppDomain.CurrentDomain.BaseDirectory;
        if (args.Length > 0 && args[0] == "--roundtrip")
        {
            // loads the settings and cheats into the form and saves them back
            using (var f = new ConfigForm(args.Length > 1 ? args[1] : root))
                return f.RoundTrip() ? 0 : 1;
        }
        if (args.Length > 1 && args[0] == "--config-preview")
        {
            using (var f = new ConfigForm(args.Length > 2 ? args[2] : root)) f.Preview(args[1]);
            return 0;
        }
        if (args.Length > 1 && args[0] == "--build-preview")
        {
            Builder.DpiAware();
            BuildForm.Preview(args[1]);
            return 0;
        }
        if (args.Contains("--build-headless"))
        {
            // automatic tests: the whole build with no window; the log is in
            // VulkanRuntime\logs (build-*.log, and build-headless-error.txt)
            Builder.Init(root);
            if (args.Contains("--compilar")) Builder.ForceFull = true;
            if (Builder.RebuildReason() == null && !args.Contains("--compilar")) return 0;
            try { new BuildJob().Run(); return 0; }
            catch (Exception e)
            {
                try { File.WriteAllText(Path.Combine(Builder.Logs, "build-headless-error.txt"), e.ToString()); } catch { }
                return 1;
            }
        }
        bool force = args.Contains("--compilar"), buildOnly = args.Contains("--solo-compilar");
        // "Jugar.exe diagnostico" (formerly JUGAR.bat diagnostico): frame timings CSV
        if (args.Any(a => a.Equals("diagnostico", StringComparison.OrdinalIgnoreCase)))
            Environment.SetEnvironmentVariable("AC4_DIAGNOSTIC", "1");
        if (args.Contains("--build-worker") || buildOnly)
        {
            // elevated compile step started by Builder.EnsureReady, or a build-only request
            Builder.DpiAware();
            Builder.Init(root);
            if (buildOnly && !args.Contains("--build-worker") && !Builder.IsElevated)
                return Builder.EnsureReady(true, true) ? 0 : 1;
            if (force || buildOnly) Builder.ForceFull = true;
            string reason = (force || buildOnly) ? "Compilación solicitada." : Builder.RebuildReason();
            if (reason == null) return (int)BuildResult.Ok;
            return (int)Builder.RunWindow(reason);
        }
        ConfigForm.ForceBuild = force;
#if PLAY_LAUNCHER
        Builder.DpiAware();
        try { using (var f = new ConfigForm(root)) f.Play(); }
        catch (Exception e) { MessageBox.Show("No se pudo iniciar AC4:\n\n" + e.Message, "Jugar AC4", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
#else
        Application.Run(new ConfigForm(root));
#endif
        return 0;
    }
}
