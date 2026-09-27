using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace Mike.Desktop;

public partial class TranslationWindow : Window
{
    private readonly NativeApiBridge api = new();
    public TranslationWindow() => InitializeComponent();
    protected override void OnClosed(EventArgs e) { api.Dispose(); base.OnClosed(e); }
    private void ChooseDocument_Click(object sender, RoutedEventArgs e) => ChooseFile(DocumentPath, "Documentos|*.txt;*.md;*.srt;*.vtt;*.docx;*.epub|Todos|*.*");
    private void ChooseMedia_Click(object sender, RoutedEventArgs e) => ChooseFile(MediaPath, "Mídia|*.mp3;*.wav;*.m4a;*.mp4;*.mkv;*.mov;*.webm|Todos|*.*");
    private static void ChooseFile(System.Windows.Controls.TextBox target, string filter) { var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true }; if (dialog.ShowDialog() == true) target.Text = dialog.FileName; }
    private async void StartManga_Click(object sender, RoutedEventArgs e) { string url = MangaUrl.Text.Trim(); if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { SetStatus("Cole uma URL válida do capítulo ou da série.", false); return; } await RunAsync("/manga/start", new { url, input = url, allowPaidApi = false }, "Tradução iniciada. A Mike preservará todas as páginas e retomará interrupções."); }
    private async void StartDocument_Click(object sender, RoutedEventArgs e) => await StartGeneralAsync(DocumentPath.Text, "document");
    private async void StartMedia_Click(object sender, RoutedEventArgs e) => await StartGeneralAsync(MediaPath.Text, "media");
    private async Task StartGeneralAsync(string input, string kind) { if (!File.Exists(input)) { SetStatus("Escolha um arquivo existente.", false); return; } await RunAsync("/translation/start", new { input, kind, project = ProjectName.Text.Trim(), source = ComboText(SourceLanguage), target = ComboText(TargetLanguage) }, "Trabalho iniciado em segundo plano. Você pode continuar usando a Mike."); }
    private static string ComboText(System.Windows.Controls.ComboBox box) => (box.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "auto";
    private async Task RunAsync(string route, object request, string success) { Busy.Visibility = Visibility.Visible; SetStatus("Preparando o motor e validando dependências...", true); try { object response = await api.HandleAsync(route, "POST", JsonSerializer.Serialize(request)); using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(response)); JsonElement root = document.RootElement; bool ok = root.TryGetProperty("ok", out var node) && node.GetBoolean(); string error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() ?? "Falha desconhecida." : "Falha desconhecida."; SetStatus(ok ? success : error, ok); } catch (Exception ex) { SetStatus("Não foi possível iniciar: " + ex.Message, false); } finally { Busy.Visibility = Visibility.Collapsed; } }
    private void SetStatus(string text, bool ok) { StatusText.Text = text; StatusText.Foreground = ok ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.LightSalmon; }
    private void OpenTranslations_Click(object sender, RoutedEventArgs e) { string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal"); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
}
