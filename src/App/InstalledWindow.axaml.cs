using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CsgoWorkshop.Core;

namespace CsgoWorkshop.App;

public sealed class MapRow
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#B3261E"));

    public MapRow(InstalledMap map)
    {
        Map = map;

        var file = new FileInfo(map.Path);
        Missing = !file.Exists;
        Size = Missing ? map.Size : file.Length;
        Details = $"Предмет {map.ItemId}, {Format.Size(Size)}, {map.InstalledAt.ToLocalTime():dd.MM.yyyy HH:mm}"
                  + (Missing ? ", файла нет в папке maps" : map.ReplacedOriginal ? ", заменила прежний файл" : "");
        DetailsBrush = Missing ? Warning : Muted;
    }

    public InstalledMap Map { get; }
    public string Name => Map.Name;
    public string Path => Map.Path;
    public bool Missing { get; }
    public bool ReplacedOriginal => Map.ReplacedOriginal;
    public long Size { get; }
    public string Details { get; }
    public IBrush DetailsBrush { get; }
}

public partial class InstalledWindow : Window
{
    private const int MaxNamesInConfirmation = 8;

    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#1B6E2F"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#B3261E"));

    private readonly MapLibrary _library;

    public InstalledWindow() : this(MapLibrary.Default())
    {
    }

    public InstalledWindow(MapLibrary library)
    {
        _library = library;
        InitializeComponent();

        MapList.SelectionChanged += (_, _) => DeleteButton.IsEnabled = MapList.SelectedItems?.Count > 0;
        MapList.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Delete)
                return;

            e.Handled = true;
            await DeleteSelectedAsync();
        };
        DeleteButton.Click += async (_, _) => await DeleteSelectedAsync();
        CloseButton.Click += (_, _) => Close();
        Opened += (_, _) =>
        {
            Activate();
            var first = MapList.IsVisible ? MapList.ContainerFromIndex(0) : null;
            (first ?? CloseButton).Focus();
        };

        Reload();
    }

    private void Reload()
    {
        var rows = _library.Load()
            .OrderByDescending(m => m.InstalledAt)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new MapRow(m))
            .ToList();

        MapList.ItemsSource = rows;
        MapList.IsVisible = rows.Count > 0;
        EmptyText.IsVisible = rows.Count == 0;
        SummaryText.IsVisible = rows.Count > 0;
        SummaryText.Text = $"Установлено этой программой: {rows.Count}, занимают {Format.Size(rows.Where(r => !r.Missing).Sum(r => r.Size))}";
        DeleteButton.IsEnabled = false;
    }

    private async Task DeleteSelectedAsync()
    {
        var rows = MapList.SelectedItems?.OfType<MapRow>().ToList() ?? [];
        if (rows.Count == 0)
            return;

        var lines = rows.Take(MaxNamesInConfirmation)
            .Select(r => r.Missing
                ? $"{r.Name}.bsp (файла нет, запись уйдёт из списка)"
                : $"{r.Name}.bsp ({Format.Size(r.Size)})" + (r.ReplacedOriginal ? ", заменила прежний файл" : ""))
            .ToList();
        if (rows.Count > MaxNamesInConfirmation)
            lines.Add($"и ещё {rows.Count - MaxNamesInConfirmation}");

        var message = (rows.Count == 1 ? "Удалить карту из папки maps?" : $"Удалить карты из папки maps ({rows.Count})?")
                      + "\n\n" + string.Join('\n', lines);
        if (rows.Any(r => r.ReplacedOriginal && !r.Missing))
            message += "\n\nПрежний файл при этом не вернётся. Если это была карта игры, восстановите её командой Steam «Проверить целостность файлов игры».";
        if (!await ConfirmDialog.AskAsync(this, "Удаление", message, "Удалить"))
            return;

        var deleted = 0;
        var errors = new List<string>();
        foreach (var row in rows)
        {
            try
            {
                _library.Remove(row.Map);
                deleted++;
            }
            catch (Exception e)
            {
                errors.Add(e.Message);
            }
        }

        Reload();

        var parts = new List<string>();
        if (deleted > 0)
            parts.Add($"Удалено: {deleted}.");
        parts.AddRange(errors);

        ResultText.Text = string.Join('\n', parts);
        ResultText.Foreground = errors.Count > 0 ? ErrorBrush : SuccessBrush;
        ResultText.IsVisible = true;
    }
}
