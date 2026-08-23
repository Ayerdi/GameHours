using System.Globalization;
using System.Windows;
using GameHours.Core.Domain;

namespace GameHours.Desktop;

public partial class SessionDetailWindow : Window
{
    private readonly DesktopSessionDetailService _service;
    private readonly Guid _sessionId;

    public SessionDetailWindow(string databasePath, Guid sessionId)
    {
        _service = new DesktopSessionDetailService(databasePath);
        _sessionId = sessionId;
        InitializeComponent();
        Loaded += SessionDetailWindow_Loaded;
    }

    private async void SessionDetailWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SessionDetailWindow_Loaded;
        try
        {
            var detail = await _service.LoadAsync(_sessionId);
            if (detail is null)
            {
                GameTitleText.Text = "Sesión no encontrada";
                TelemetryText.Text = "La sesión ya no existe en la base de datos local.";
                return;
            }

            Apply(detail);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            GameTitleText.Text = "No se pudo cargar la sesión";
            TelemetryText.Text = exception.Message;
        }
    }

    private void Apply(DesktopSessionDetail detail)
    {
        GameTitleText.Text = detail.GameTitle;
        var start = detail.StartedAtUtc.ToLocalTime();
        var end = detail.EndedAtUtc.ToLocalTime();
        DateText.Text = start.ToString("dddd, d 'de' MMMM 'de' yyyy", CultureInfo.CurrentCulture);
        StartText.Text = start.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        EndText.Text = end.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        DurationText.Text = FormatDuration(detail.Duration);
        FocusedText.Text = FormatOptional(detail.FocusedDuration);
        ActiveText.Text = FormatOptional(detail.ActiveDuration);
        AfkText.Text = FormatOptional(detail.AfkDuration);
        UnfocusedText.Text = FormatOptional(detail.UnfocusedOrUnknownDuration);
        AfkThresholdText.Text = detail.AfkFilterEnabled && detail.IdleThreshold is TimeSpan threshold
            ? FormatDuration(threshold)
            : "Desactivado";
        CaptureText.Text = $"Captura: {CaptureName(detail.CaptureMethod)} · confianza {ConfidenceName(detail.Confidence)}";
        EndReasonText.Text = $"Fin: {EndReasonName(detail.EndReason)}";
        TelemetryText.Text = !detail.HasActivityTelemetry
            ? "Esta sesión es anterior a la telemetría de atención o no tiene muestras persistidas; foco, activo y AFK quedan como no disponibles."
            : detail.AfkFilterEnabled
                ? "Foco, activo estimado y AFK se calcularon durante esta sesión con el umbral indicado."
                : "Se midió el tiempo en primer plano, pero el filtro AFK estaba desactivado: activo estimado y AFK no existen para esta sesión.";
        SessionIdText.Text = $"ID de sesión · {detail.SessionId:D}";
    }

    private static string FormatOptional(TimeSpan? value) => value is TimeSpan duration ? FormatDuration(duration) : "—";

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1)) return duration <= TimeSpan.Zero ? "0 s" : "<1 s";
        if (duration < TimeSpan.FromMinutes(1)) return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))} s";
        var totalMinutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (hours == 0) return $"{minutes} min";
        return minutes == 0 ? $"{hours} h" : $"{hours} h {minutes} min";
    }

    private static string CaptureName(CaptureMethod value) => value switch
    {
        CaptureMethod.Wmi => "evento de Windows",
        CaptureMethod.Reconciliation => "reconciliación",
        CaptureMethod.InitialSnapshot => "snapshot inicial",
        CaptureMethod.Etw => "ETW",
        _ => value.ToString()
    };

    private static string ConfidenceName(Confidence value) => value switch
    {
        Confidence.Exact => "exacta",
        Confidence.High => "alta",
        Confidence.Estimated => "estimada",
        _ => value.ToString()
    };

    private static string EndReasonName(string? value) => value switch
    {
        "GracefulShutdown" => "GameHours se cerró limpiamente",
        "RecoveredFromCheckpoint" => "recuperada desde checkpoint",
        "ReconciledStop" or "Stopped" => "juego cerrado",
        null or "" => "fin medido",
        _ => value
    };
}
