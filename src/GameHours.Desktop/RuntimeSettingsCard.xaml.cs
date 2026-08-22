using System.Windows;
using System.Windows.Controls;

namespace GameHours.Desktop;

public partial class RuntimeSettingsCard : UserControl
{
    public sealed record AfkChoice(int Minutes, string DisplayName);

    private static readonly IReadOnlyList<AfkChoice> Choices =
    [
        new(0, "Desactivado"),
        new(2, "2 minutos"),
        new(5, "5 minutos · Recomendado"),
        new(10, "10 minutos"),
        new(15, "15 minutos")
    ];

    private readonly DesktopHost _host;
    private bool _initializing;
    private bool _saving;

    public RuntimeSettingsCard(DesktopHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeComponent();

        _initializing = true;
        try
        {
            AfkComboBox.ItemsSource = Choices;
            ApplyPreferences(_host.Preferences);
        }
        finally
        {
            _initializing = false;
        }

        _host.PreferencesChanged += Host_PreferencesChanged;
        Unloaded += RuntimeSettingsCard_Unloaded;
    }

    public event Action? DiagnosticsRequested;
    public event Action? ExecutableManagementRequested;

    private void Host_PreferencesChanged(DesktopPreferences preferences)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Host_PreferencesChanged(preferences)));
            return;
        }

        _initializing = true;
        try { ApplyPreferences(preferences); }
        finally { _initializing = false; }
    }

    private void ApplyPreferences(DesktopPreferences preferences)
    {
        AfkComboBox.SelectedItem = Choices.First(choice =>
            choice.Minutes == preferences.AfkTimeoutMinutes);
        LowImpactCheckBox.IsChecked = preferences.LowImpactMode;
        UpdateAfkExplanation(preferences.AfkTimeoutMinutes);
        SaveStatusText.Text = preferences.LowImpactMode
            ? "Impacto mínimo activado · el trabajo no esencial espera mientras juegas."
            : "Impacto mínimo desactivado.";
    }

    private async void AfkComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _saving || AfkComboBox.SelectedItem is not AfkChoice choice)
        {
            return;
        }

        UpdateAfkExplanation(choice.Minutes);
        await SaveAsync(choice.Minutes, LowImpactCheckBox.IsChecked == true);
    }

    private async void LowImpactCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _saving || AfkComboBox.SelectedItem is not AfkChoice choice)
        {
            return;
        }

        await SaveAsync(choice.Minutes, LowImpactCheckBox.IsChecked == true);
    }

    private async Task SaveAsync(int afkMinutes, bool lowImpactMode)
    {
        _saving = true;
        AfkComboBox.IsEnabled = false;
        LowImpactCheckBox.IsEnabled = false;
        SaveStatusText.Text = "Guardando preferencia local…";

        try
        {
            var result = await _host.ApplyPreferencesAsync(
                new DesktopPreferences(afkMinutes, lowImpactMode));

            SaveStatusText.Text = result.DeferredUntilIdle
                ? "Guardado · el nuevo criterio AFK se aplicará cuando termine la sesión actual."
                : lowImpactMode
                    ? "Guardado · impacto mínimo activado."
                    : "Guardado.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SaveStatusText.Text = $"No se pudo guardar: {exception.Message}";
            _initializing = true;
            try { ApplyPreferences(_host.Preferences); }
            finally { _initializing = false; }
        }
        finally
        {
            _saving = false;
            AfkComboBox.IsEnabled = true;
            LowImpactCheckBox.IsEnabled = true;
        }
    }

    private void UpdateAfkExplanation(int minutes)
    {
        AfkExplanationText.Text = minutes == 0
            ? "No consulta inactividad de teclado/ratón ni mando. GameHours conserva el tiempo en primer plano y “activo” lo refleja, sin estimar AFK."
            : $"Si el juego sigue en primer plano pero no detectamos actividad durante {minutes} min, el tiempo activo estimado deja de aumentar. No se guardan teclas, clics, posiciones ni botones.";
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e) =>
        DiagnosticsRequested?.Invoke();

    private void ManageExecutables_Click(object sender, RoutedEventArgs e) =>
        ExecutableManagementRequested?.Invoke();

    private void RuntimeSettingsCard_Unloaded(object sender, RoutedEventArgs e)
    {
        _host.PreferencesChanged -= Host_PreferencesChanged;
        Unloaded -= RuntimeSettingsCard_Unloaded;
    }
}
