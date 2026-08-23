using System.Globalization;
using System.Windows;

namespace GameHours.Desktop;

public partial class RuntimeDiagnosticsWindow : Window
{
    private readonly DesktopHost _host;

    public RuntimeDiagnosticsWindow(DesktopHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeComponent();
        Loaded += (_, _) => RefreshSnapshot();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSnapshot();

    private void RefreshSnapshot()
    {
        var snapshot = _host.GetRuntimeDiagnostics();
        TrackingText.Text = snapshot.IsTracking
            ? snapshot.StatusText
            : $"Detenido · {snapshot.StatusText}";
        ActiveGameText.Text = snapshot.ActiveGameTitle ?? "Ninguno";

        var monitor = snapshot.ProcessMonitor;
        ProcessModeText.Text = !monitor.IsRunning
            ? "Monitor detenido"
            : monitor.EventDrivenActive
                ? "Eventos de Windows + red de seguridad"
                : monitor.DegradedFallback
                    ? "Fallback de reconciliación"
                    : "Inicializando";
        ProcessDetailText.Text = !monitor.IsRunning
            ? "Sin observación activa de procesos."
            : monitor.EventDrivenActive
                ? $"{monitor.ProcessStartEvents.ToString(CultureInfo.InvariantCulture)} avisos de arranque procesados · snapshot global de seguridad cada 5 s."
                : monitor.DegradedFallback
                    ? "WMI no está disponible: se prioriza fiabilidad y se vuelve al snapshot global cada 1 s."
                    : "Preparando la observación de procesos.";

        ReconciliationText.Text = monitor.FullReconciliations == 1
            ? "1 reconciliación completa"
            : $"{monitor.FullReconciliations.ToString(CultureInfo.InvariantCulture)} reconciliaciones completas";
        LastReconciliationText.Text = monitor.LastReconciliationAtUtc is DateTimeOffset last
            ? $"Última: {last.ToLocalTime():HH:mm:ss}"
            : "Todavía no hay reconciliación registrada.";

        AfkPolicyText.Text = BuildAfkPolicyText(snapshot);
        LowImpactText.Text = snapshot.Preferences.LowImpactMode
            ? "Impacto mínimo: activado. Los refrescos no esenciales se aplazan mientras juegas."
            : "Impacto mínimo: desactivado. Los refrescos no esenciales pueden ejecutarse durante una partida.";

        MemoryText.Text = FormatBytes(snapshot.WorkingSetBytes);
        CpuText.Text = FormatCpu(snapshot.ProcessCpuTime);
        ThreadText.Text = snapshot.ThreadCount.ToString(CultureInfo.InvariantCulture);
        DatabasePathText.Text = $"Base de datos: {snapshot.DatabasePath}";
        PreferencesPathText.Text = $"Preferencias: {snapshot.PreferencesPath}";
    }

    private static string BuildAfkPolicyText(DesktopRuntimeDiagnostics snapshot)
    {
        var configured = snapshot.Preferences.AfkTimeoutMinutes;
        if (snapshot.AppliedAfkTimeoutMinutes is not int applied)
        {
            return $"AFK configurado: {FormatAfk(configured)}. El tracker está detenido; se aplicará al iniciarlo.";
        }

        if (applied != configured)
        {
            return $"AFK configurado: {FormatAfk(configured)}. La sesión actual sigue usando {FormatAfk(applied)}; el cambio se aplicará cuando termine y el tracker reinicie de forma segura.";
        }

        return configured > 0
            ? $"AFK: {configured} min. Se consulta sólo una señal de inactividad mientras hay un juego activo."
            : "AFK: desactivado. No se consulta inactividad de teclado/ratón ni XInput; sólo se mide si el juego está en primer plano.";
    }

    private static string FormatAfk(int minutes) =>
        minutes > 0 ? $"{minutes} min" : "desactivado";

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        var mebibytes = bytes / (1024d * 1024d);
        return $"{mebibytes.ToString("0.0", CultureInfo.CurrentCulture)} MiB";
    }

    private static string FormatCpu(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) return "0 s";
        if (value.TotalMinutes < 1)
            return $"{value.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s";
        return $"{value.TotalMinutes.ToString("0.0", CultureInfo.CurrentCulture)} min";
    }
}
