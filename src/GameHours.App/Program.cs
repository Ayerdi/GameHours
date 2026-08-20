using GameHours.Storage.Sqlite;
using GameHours.Windows.Processes;

var dataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "GameHours");
var databasePath = Path.Combine(dataDirectory, "gamehours.db");

var database = new GameHoursDatabase(databasePath);
await database.InitializeAsync();

var trackingState = new SqliteTrackingStateRepository(database);
var trackingStartedAt = await trackingState.GetTrackingStartedAtAsync();

var snapshotProvider = new WindowsProcessSnapshotProvider();
var processes = await snapshotProvider.GetSnapshotAsync();

Console.WriteLine("GameHours development host");
Console.WriteLine($"Database: {database.DatabasePath}");
Console.WriteLine($"Tracking cutover: {(trackingStartedAt is null ? "<not started>" : trackingStartedAt.Value.ToString("O"))}");
Console.WriteLine($"Visible processes: {processes.Count}");
Console.WriteLine("No playtime is recorded by this host yet; process monitoring is the next slice.");
