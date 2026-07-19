using System.Text;
using System.Windows;
using System.Windows.Controls;
using CrxMem.MemoryView.Wpf.Services;

namespace CrxMem.MemoryView.Wpf.Views;

public partial class AutoAssemblerWindow : Window
{
    private readonly IAutoAssemblerService _service;
    public AutoAssemblerWindow(IAutoAssemblerService service)
    {
        _service = service;
        InitializeComponent();
        sourceEditor.Text = "[ENABLE]\n// Select a template or enter an AA script\n\n[DISABLE]\n";
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await PreviewAsync(true, false);
    private async void Preview_Click(object sender, RoutedEventArgs e) => await PreviewAsync(true, true);
    private async Task PreviewAsync(bool enable, bool showPlan)
    {
        SetBusy(true, "Compiling without changing the target…");
        try
        {
            AutoAssemblerCompileResult result = await _service.CheckAsync(sourceEditor.Text, enable, CancellationToken.None);
            diagnosticsList.ItemsSource = result.Diagnostics.Select(d => $"{d.Severity} {d.Code} L{d.Span.Line}: {d.Message}");
            previewText.Text = showPlan && result.Plan != null ? FormatPlan(result.Plan) : string.Empty;
            statusText.Text = result.Succeeded ? "Syntax and binding succeeded. Preview did not mutate the target." : "Compilation failed; nothing was written.";
        }
        finally { SetBusy(false, statusText.Text); }
    }

    private async void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this, "Apply this process-bound plan? Assertions and original bytes will be verified before writes.",
                "Enable Auto Assembler script", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        SetBusy(true, "Enabling…");
        try { AutoAssemblerExecutionResult result = await _service.EnableAsync(sourceEditor.Text, CancellationToken.None); statusText.Text = result.Message; }
        finally { SetBusy(false, statusText.Text); }
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Disabling…");
        try { AutoAssemblerExecutionResult result = await _service.DisableAsync(sourceEditor.Text, CancellationToken.None); statusText.Text = result.Message; }
        finally { SetBusy(false, statusText.Text); }
    }

    private void TemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || templateBox.SelectedItem is not ComboBoxItem item) return;
        sourceEditor.Text = item.Content?.ToString() switch
        {
            "Code injection" => "[ENABLE]\nassert(module.exe+1234,48 89 5C 24 08)\nalloc(newmem,1000,module.exe+1234)\nnewmem:\n  // code\nmodule.exe+1234:\n  jmp newmem\n\n[DISABLE]\nmodule.exe+1234:\n  db 48 89 5C 24 08\ndealloc(newmem)",
            "AOB injection" => "[ENABLE]\naobscanmodule(INJECT,module.exe,48 89 5C 24 08)\nassert(INJECT,48 89 5C 24 08)\nalloc(newmem,1000,INJECT)\nnewmem:\n  // code\nINJECT:\n  jmp newmem\n\n[DISABLE]\nINJECT:\n  db 48 89 5C 24 08\ndealloc(newmem)",
            "Full injection" => "[ENABLE]\nalloc(newmem,2000)\nlabel(return)\nregistersymbol(newmem)\nnewmem:\n  // full injection body\n  jmp return\n\n[DISABLE]\nunregistersymbol(newmem)\ndealloc(newmem)",
            _ => sourceEditor.Text
        };
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
    private void SetBusy(bool busy, string status) { IsEnabled = !busy; statusText.Text = status; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
