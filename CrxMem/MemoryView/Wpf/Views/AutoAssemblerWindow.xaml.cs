using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CrxMem.MemoryView.Wpf.Services;

namespace CrxMem.MemoryView.Wpf.Views;

public partial class AutoAssemblerWindow : Window
{
    private readonly IAutoAssemblerService _service;
    private readonly AutoAssemblerEditorContext? _context;
    private static readonly string LayoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrxMem", "AutoAssembler.layout.json");

    public AutoAssemblerWindow(IAutoAssemblerService service, AutoAssemblerEditorContext? context = null)
    {
        _service = service;
        _context = context;
        InitializeComponent();
        sourceEditor.Text = "[ENABLE]\n// Select a template or enter an AA script\n\n[DISABLE]\n";
        Loaded += (_, _) => RestoreLayout();
        Closing += (_, _) => SaveLayout();
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await PreviewAsync(true, false);
    private async void Preview_Click(object sender, RoutedEventArgs e) => await PreviewAsync(true, true);

    private async Task PreviewAsync(bool enable, bool showPlan)
    {
        SetBusy(true, "Compiling without changing the target...");
        try
        {
            AutoAssemblerCompileResult result = await _service.CheckAsync(sourceEditor.Text, enable, CancellationToken.None);
            diagnosticsList.ItemsSource = result.Diagnostics.Select(AutoAssemblerDiagnosticRow.From).ToArray();
            previewText.Text = showPlan && result.Plan != null ? FormatPlan(result.Plan) : string.Empty;
            statusText.Text = result.Succeeded
                ? "Syntax and binding succeeded. Preview did not mutate the target."
                : "Compilation failed; nothing was written.";
        }
        finally { SetBusy(false, statusText.Text); }
    }

    private async void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (sourceEditor.Text.Contains("{$LUA}", StringComparison.OrdinalIgnoreCase) &&
            sourceEditor.Text.Contains("autoAssemble(", StringComparison.OrdinalIgnoreCase) &&
            (_service.TrustedLuaCapabilities & AutoAssemblerCapability.ProcessMutation) == 0)
        {
            if (System.Windows.MessageBox.Show(this, _service.LuaCapabilitySummary +
                    "\n\nThis script requests nested process mutation through autoAssemble(). Trust that callback for this MemoryView session?",
                    "AA Lua capabilities", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _service.SetTrustedLuaCapabilities(_service.TrustedLuaCapabilities | AutoAssemblerCapability.ProcessMutation);
        }
        if (System.Windows.MessageBox.Show(this, "Apply this process-bound plan? Assertions and original bytes will be verified before writes.",
                "Enable Auto Assembler script", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        SetBusy(true, "Enabling...");
        try
        {
            AutoAssemblerExecutionResult result = await _service.EnableAsync(sourceEditor.Text, CancellationToken.None);
            statusText.Text = result.Message;
        }
        finally { SetBusy(false, statusText.Text); }
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Disabling...");
        try
        {
            AutoAssemblerExecutionResult result = await _service.DisableAsync(sourceEditor.Text, CancellationToken.None);
            statusText.Text = result.Message;
        }
        finally { SetBusy(false, statusText.Text); }
    }

    private void TemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || templateBox.SelectedItem is not ComboBoxItem item) return;
        sourceEditor.Text = item.Content?.ToString() switch
        {
            "Code injection" => BuildCodeInjectionTemplate(),
            "AOB injection" => BuildAobInjectionTemplate(),
            "Full injection" => BuildFullInjectionTemplate(),
            _ => sourceEditor.Text
        };
    }

    private string Header => _context == null ? "// Architecture unavailable: select [32-BIT] or [64-BIT] before compiling.\n"
        : _context.Bitness == 32 ? "[32-BIT]\n" : "[64-BIT]\n";
    private string? ContextExpression => string.IsNullOrWhiteSpace(_context?.AddressExpression) ? null : _context!.AddressExpression;
    private string? ContextBytes => NormalizeBytes(_context?.OriginalBytes);

    private string BuildCodeInjectionTemplate()
    {
        if (ContextExpression is not { } expression || ContextBytes is not { } bytes)
            return Header + "// Select a decoded instruction before creating a code-injection template.\n[ENABLE]\n\n[DISABLE]\n";
        return $"{Header}[ENABLE]\nassert({expression},{bytes})\nalloc(newmem,1000,{expression})\nlabel(return)\nnewmem:\n  // injected code\n  jmp return\n{expression}:\n  jmp newmem\nreturn:\n\n[DISABLE]\n{expression}:\n  db {bytes}\ndealloc(newmem)";
    }

    private string BuildAobInjectionTemplate()
    {
        if (string.IsNullOrWhiteSpace(_context?.ModuleName) || ContextBytes is not { } bytes)
            return Header + "// Select a module-backed instruction before creating an AOB template.\n[ENABLE]\n\n[DISABLE]\n";
        return $"{Header}[ENABLE]\naobscanmodule(INJECT,{_context!.ModuleName},{bytes})\nassert(INJECT,{bytes})\nalloc(newmem,1000,INJECT)\nlabel(return)\nnewmem:\n  // injected code\n  jmp return\nINJECT:\n  jmp newmem\nreturn:\nregistersymbol(INJECT)\n\n[DISABLE]\nINJECT:\n  db {bytes}\nunregistersymbol(INJECT)\ndealloc(newmem)";
    }

    private string BuildFullInjectionTemplate()
    {
        string near = ContextExpression is { } expression ? $",{expression}" : string.Empty;
        string note = ContextExpression == null ? "// No selected instruction; allocation is not constrained to a nearby address.\n" : string.Empty;
        return $"{Header}{note}[ENABLE]\nalloc(newmem,2000{near})\nregistersymbol(newmem)\nnewmem:\n  // full injection body\n\n[DISABLE]\nunregistersymbol(newmem)\ndealloc(newmem)";
    }

    private static string? NormalizeBytes(string? bytes)
    {
        if (string.IsNullOrWhiteSpace(bytes) || bytes == "—") return null;
        string normalized = string.Join(' ', bytes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.All(c => char.IsAsciiHexDigit(c) || char.IsWhiteSpace(c)) ? normalized : null;
    }

    private void DiagnosticsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (diagnosticsList.SelectedItem is not AutoAssemblerDiagnosticRow row) return;
        int line = Math.Clamp(row.Line - 1, 0, Math.Max(0, sourceEditor.LineCount - 1));
        int start = sourceEditor.GetCharacterIndexFromLineIndex(line);
        if (start < 0) return;
        int selectionStart = Math.Min(sourceEditor.Text.Length, start + Math.Max(0, row.Column - 1));
        int length = Math.Max(1, Math.Min(row.Length, Math.Max(1, sourceEditor.GetLineLength(line))));
        sourceEditor.Focus();
        sourceEditor.Select(selectionStart, Math.Min(length, sourceEditor.Text.Length - selectionStart));
        sourceEditor.ScrollToLine(line);
    }

    private void CopyDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (diagnosticsList.SelectedItem is AutoAssemblerDiagnosticRow row) System.Windows.Clipboard.SetText(row.ToString());
    }

    private void CopyAllDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (diagnosticsList.Items.Count == 0) return;
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, diagnosticsList.Items.Cast<AutoAssemblerDiagnosticRow>()));
    }

    private static string FormatPlan(AutoAssemblerPlan plan)
    {
        var text = new StringBuilder();
        text.AppendLine($"Mode: {(plan.IsEnable ? "ENABLE" : "DISABLE")}  {plan.Bitness}-bit");
        text.AppendLine($"Process: {plan.ProcessId} @ {plan.ProcessStartTimeUtc:O}");
        foreach (AutoAssemblerAllocation a in plan.Allocations) text.AppendLine($"ALLOC {a.Name} 0x{a.Address:X} size 0x{a.Size:X}");
        foreach (AutoAssemblerAssertion a in plan.Assertions) text.AppendLine($"ASSERT 0x{a.Address:X}: {Convert.ToHexString(a.ExpectedBytes)}");
        foreach (AutoAssemblerWrite w in plan.Writes) text.AppendLine($"WRITE 0x{w.Address:X}: {Convert.ToHexString(w.Bytes)}");
        return text.ToString();
    }

    private void RestoreLayout()
    {
        try
        {
            if (!File.Exists(LayoutPath)) return;
            AaLayout? layout = JsonSerializer.Deserialize<AaLayout>(File.ReadAllText(LayoutPath));
            if (layout == null) return;
            Width = Math.Max(MinWidth, layout.Width);
            Height = Math.Max(MinHeight, layout.Height);
            if (layout.DiagnosticsWidth > 120) diagnosticsColumn.Width = new GridLength(layout.DiagnosticsWidth);
        }
        catch { }
    }

    private void SaveLayout()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            string temporary = LayoutPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new AaLayout(ActualWidth, ActualHeight, diagnosticsColumn.ActualWidth)));
            File.Move(temporary, LayoutPath, true);
        }
        catch { }
    }

    private void SetBusy(bool busy, string status) { IsEnabled = !busy; statusText.Text = status; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record AaLayout(double Width, double Height, double DiagnosticsWidth);
    private sealed record AutoAssemblerDiagnosticRow(string Severity, string Code, string Location, string Message,
        int Line, int Column, int Length)
    {
        public static AutoAssemblerDiagnosticRow From(AutoAssemblerDiagnostic diagnostic) => new(
            diagnostic.Severity.ToString(), diagnostic.Code, $"L{diagnostic.Span.Line}:C{diagnostic.Span.Column}",
            diagnostic.Message, diagnostic.Span.Line, diagnostic.Span.Column, diagnostic.Span.Length);
        public override string ToString() => $"{Severity} {Code} {Location}: {Message}";
    }
}
