using System.Diagnostics;
using System.Globalization;
using System.Windows;

namespace GameHours.Desktop;

public partial class RuntimeDiagnosticsWindow : Window
{
    private static readonly TimeSpan CpuSampleInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan RuntimeMeasurementDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RuntimeMeasurementSampleInterval = TimeSpan.FromSeconds(1);
    private readonly DesktopHost _host;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshing;

    public RuntimeDiagnosticsWindow(DesktopHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeComponent();
        Loaded += async (_, _) => await RefreshSnapshotAsync();
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshSnapshotAsync();

    private async void Measure_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        SetBusy(true);
        MeasurementText.Text = "Midiendo durante 30 s… mantén estable el estado que quieras comparar.";

        try
        {
            var measurement = await DesktopRuntimeMeasurementSampler.MeasureAsync(
                _host.GetRuntimeDiagnostics,
                RuntimeMeasurementDuration,
                RuntimeMeasurementSampleInterval,
                _lifetime.Token);
            MeasurementText.Text = FormatMeasurement(measurement);
            ApplySnapshot(_host.GetRuntimeDiagnostics());
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            if (!_lifetime.IsCancellationRequested) SetBusy(false);
        }
    }

    private async Task RefreshSnapshotAsync()
    {
        if (_refreshing) return;
        SetBusy(true);
        CpuText.Text = "Midiendo…";

        try
        {
            var first = _host.GetRuntimeDiagnostics();
            ApplySnapshot(first);
            CpuText.Text = "Midiendo…";

            var started = Stopwatch.GetTimestamp();
            await Task.Delay(CpuSampleInterval, _lifetime.Token);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var second = _host.GetRuntimeDiagnostics();
            ApplySnapshot(second);
            CpuText.Text = FormatCpuPercent(first.ProcessCpuTime, second.ProcessCpuTime, elapsed);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            if (!_lifetime.IsCancellationRequested) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _refreshing = busy;
        RefreshButton.IsEnabled = !busy;
        MeasureButton.IsEnabled = !busy;
    }

    private void ApplySnapshot(DesktopRuntimeDiagnostics snapshot)
    {
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
            ? "Impacto mínimo: activado. Mientras juegas se posponen refrescos de vistas y búsquedas de actualizaciones; tracking, logros y guardado continúan igual."
            : "Impacto mínimo: desactivado. Las tareas no esenciales pueden ejecutarse también durante una partida.";

        PrivateMemoryText.Text = FormatBytes(snapshot.PrivateMemoryBytes);
        WorkingSetText.Text = FormatBytes(snapshot.WorkingSetBytes);
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

    private static string FormatMeasurement(DesktopRuntimeMeasurement measurement)
    {
        var reconciliations = measurement.ReconciliationDelta is long delta
            ? $"+{delta.ToString(CultureInfo.InvariantCulture)}"
            : "—";
        var threads = measurement.AverageThreadCount is double averageThreads
            ? $"{averageThreads.ToString("0.0", CultureInfo.CurrentCulture)} media / {measurement.PeakThreadCount?.ToString(CultureInfo.InvariantCulture) ?? "—"} pico"
            : "—";
        var collections = $"G0 {FormatDelta(measurement.Gen0CollectionDelta)} / " +
                          $"G1 {FormatDelta(measurement.Gen1CollectionDelta)} / " +
                          $"G2 {FormatDelta(measurement.Gen2CollectionDelta)}";

        return $"{measurement.Duration.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s · " +
               $"CPU media {FormatPercent(measurement.CpuPercent)} · " +
               $"Memoria privada {FormatBytes(measurement.AveragePrivateMemoryBytes)} media / {FormatBytes(measurement.PeakPrivateMemoryBytes)} pico · " +
               $"Working set {FormatBytes(measurement.AverageWorkingSetBytes)} media / {FormatBytes(measurement.PeakWorkingSetBytes)} pico · " +
               $"Hilos {threads} · Reconciliaciones {reconciliations}.\n" +
               $"GC gestionado: heap {FormatBytes(measurement.AverageManagedHeapBytes)} media / {FormatBytes(measurement.PeakManagedHeapBytes)} pico · " +
               $"Asignación {FormatByteRate(measurement.ManagedAllocationRateBytesPerSecond)} · Pausa {FormatPercent(measurement.GcPausePercent)} · " +
               $"Colecciones {collections}.\n" +
               $"Memoria GC: comprometida {FormatBytes(measurement.PeakGcCommittedBytes)} pico · " +
               $"fragmentada {FormatBytesIncludingZero(measurement.PeakGcFragmentedBytes)} pico.";
    }

    private static string FormatAfk(int minutes) => minutes > 0 ? $"{minutes} min" : "desactivado";

    private static string FormatBytes(long bytes) => FormatBytes((long?)bytes);

    private static string FormatBytes(long? bytes)
    {
        if (bytes is not > 0) return "—";
        var mebibytes = bytes.Value / (1024d * 1024d);
        return $"{mebibytes.ToString("0.0", CultureInfo.CurrentCulture)} MiB";
    }

    private static string FormatBytesIncludingZero(long? bytes)
    {
        if (bytes is not long value || value < 0) return "—";
        var mebibytes = value / (1024d * 1024d);
        return $"{mebibytes.ToString("0.0", CultureInfo.CurrentCulture)} MiB";
    }

    private static string FormatByteRate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not double value || value < 0d) return "—";
        var mebibytesPerSecond = value / (1024d * 1024d);
        return $"{mebibytesPerSecond.ToString(mebibytesPerSecond < 1d ? "0.00" : "0.0", CultureInfo.CurrentCulture)} MiB/s";
    }

    private static string FormatDelta(int? value) =>
        value is int delta ? $"+{delta.ToString(CultureInfo.InvariantCulture)}" : "—";

    private static string FormatPercent(double? percent) =>
        percent is double value
            ? $"{value.ToString(value < 1d ? "0.00" : "0.0", CultureInfo.CurrentCulture)} %"
            : "—";

    private static string FormatCpuPercent(TimeSpan before, TimeSpan after, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || after < before) return "—";
        var cpuSeconds = (after - before).TotalSeconds;
        var capacitySeconds = elapsed.TotalSeconds * Math.Max(1, Environment.ProcessorCount);
        var percent = Math.Clamp(cpuSeconds / capacitySeconds * 100d, 0d, 100d);
        return FormatPercent(percent);
    }
}
