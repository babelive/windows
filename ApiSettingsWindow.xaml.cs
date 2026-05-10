using System.Windows;
using Babelive.Translation;
using MessageBox = System.Windows.MessageBox;

namespace Babelive;

/// <summary>
/// Modal dialog for editing the API endpoint base URL and API key.
/// On Save, mutates the supplied <see cref="AppSettings"/> in place AND
/// persists to <c>%APPDATA%\Babelive\settings.json</c>; on Cancel,
/// the supplied settings instance is left untouched.
/// </summary>
public partial class ApiSettingsWindow : Window
{
    private readonly AppSettings _settings;

    public ApiSettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        // Pre-fill the official base so users see a concrete starting
        // value to edit. We still treat empty after Save as "use default";
        // a literal match of DefaultBase persists as that string but
        // produces the same URLs either way (BuildXxxUrl normalizes both).
        BaseUrlBox.Text = string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? RealtimeTranslatorClient.DefaultBase
            : settings.BaseUrl;
        ApiKeyBox.Text = settings.ApiKey ?? string.Empty;
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        // Trim and normalize to null for empty — keeps "use built-in
        // default" semantics distinct from "set explicitly to empty
        // string".
        _settings.BaseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text)
            ? null : BaseUrlBox.Text.Trim();
        _settings.ApiKey  = string.IsNullOrWhiteSpace(ApiKeyBox.Text)
            ? null : ApiKeyBox.Text.Trim();
        try
        {
            _settings.Save();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to save settings to:\n{AppSettings.FilePath}\n\n{ex.Message}",
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
