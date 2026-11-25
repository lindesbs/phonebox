using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace PhoneBox.Agent;

/// <summary>An open task. Its list and file stay on this computer: the phone opens a task by its id.</summary>
public sealed record NcTask(string Id, string Title, string List, long? Due, bool AllDay, int Priority,
    [property: JsonIgnore] string Calendar, [property: JsonIgnore] string File);

public sealed record NcTasksView(IReadOnlyList<NcTask> Tasks, string? Error, bool CanOpen, long Updated);

/// <summary>
/// Open tasks from a Nextcloud account over CalDAV, read-only, for the Nextcloud tasks widget. The address, user and app
/// password are options of the widget and never reach the phone (StackWidgets.PublicOptions). The account is read only
/// while a phone asks for a widget on an enabled page, at most once a minute per widget. With the widget's "open" option
/// on, a tap opens the task in the Nextcloud Tasks app in this computer's browser.
/// </summary>
public sealed class NextcloudTasks(ConfigStore config, ILogger<NextcloudTasks> log)
{
    internal const string WidgetType = "nextcloud-tasks";
    private const int MaxTasks = 200;
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(55);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 8 << 20 };
    private static readonly XNamespace Dav = "DAV:", Cal = "urn:ietf:params:xml:ns:caldav";

    /// <summary>Test seam: opens a task on this computer.</summary>
    internal Action<Uri> Opener { get; init; } = FeedMonitor.OpenInBrowser;

    private readonly ConcurrentDictionary<string, (DateTime At, string Account, NcTasksView View)> _cache = new();

    public async Task<NcTasksView> ViewAsync(string? deviceId, string? widget, CancellationToken ct)
    {
        if (PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType) is not { } w) return new([], "This widget is not on a page of this phone", false, 0);
        var canOpen = w.Bool("open") == true;
        if (Account(w) is not { } acct) return new([], "Enter the Nextcloud address, user and app password in the widget's settings", canOpen, 0);
        var sig = string.Join('\n', acct.Base, acct.User, acct.Password, string.Join('|', acct.Lists));
        _cache.TryGetValue(widget!, out var last);
        if (last.Account == sig && DateTime.UtcNow - last.At < MaxAge) return last.View with { CanOpen = canOpen };
        NcTasksView view;
        try
        {
            view = new(await FetchAsync(acct, ct), null, canOpen, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var msg = ex is TaskCanceledException ? "Nextcloud did not answer in 20 s" : ex.Message;
            log.LogInformation("nextcloud tasks: {Msg}", msg);
            // the last good list of the same account stays, with the error beside it
            view = new(last.Account == sig ? last.View.Tasks : [], msg, canOpen, last.Account == sig ? last.View.Updated : 0);
        }
        _cache[widget!] = (DateTime.UtcNow, sig, view);
        return view;
    }

    /// <summary>Opens a task from this widget's current list in the browser; returns an error message, or null when it worked.</summary>
    public string? Open(string? deviceId, string? widget, string? id)
    {
        var w = PanelWidgets.Find(config.Get(), deviceId, widget, WidgetType);
        if (w?.Bool("open") != true) return "Opening tasks is off: turn on \"Open tasks on the PC\" in the widget's settings";
        if (Account(w) is not { } acct) return "The widget has no Nextcloud account";
        if (string.IsNullOrEmpty(id) || !_cache.TryGetValue(widget!, out var c) || c.View.Tasks.FirstOrDefault(t => t.Id == id) is not { } task)
            return "That task is no longer in the list";
        var uri = TaskUri(acct.Base, task.Calendar, task.File);
        try { Opener(uri); }
        catch (Exception ex) { log.LogWarning("nextcloud tasks: could not open {Host}: {Msg}", uri.Host, ex.Message); return "Could not open it on the computer"; }
        return null;
    }

    /// <summary>The Tasks app's own link to a task (router path /calendars/:calendarId/tasks/:taskId, Tasks 0.16 and later).</summary>
    internal static Uri TaskUri(Uri baseUri, string calendar, string file) =>
        new(baseUri, $"apps/tasks/calendars/{Seg(calendar)}/tasks/{Seg(file)}");

    private static string Seg(string s) => Uri.EscapeDataString(Uri.UnescapeDataString(s));

    internal sealed record NcAccount(Uri Base, string User, string Password, string[] Lists);

    internal static NcAccount? Account(WidgetConfig w)
    {
        var url = w.Str("url")?.Trim() ?? "";
        var user = w.Str("user")?.Trim() ?? "";
        var pass = w.Str("password")?.Trim() ?? "";
        if (user.Length == 0 || pass.Length == 0 || !Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var b)
            || b.Scheme is not ("https" or "http")) return null;
        var lists = w.Opt("lists") is { ValueKind: System.Text.Json.JsonValueKind.Array } arr
            ? arr.EnumerateArray().Select(x => x.ToString().Trim()).Where(x => x.Length > 0).ToArray() : [];
        return new(b, user, pass, lists);
    }

    private static async Task<List<NcTask>> FetchAsync(NcAccount a, CancellationToken ct)
    {
        var home = new Uri(a.Base, $"remote.php/dav/calendars/{Uri.EscapeDataString(a.User)}/");
        var lists = (await DavAsync(a, "PROPFIND", home,
                "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><d:resourcetype/><d:displayname/><c:supported-calendar-component-set/></d:prop></d:propfind>", ct))
            .Descendants(Dav + "response").Select(r =>
            {
                var prop = r.Descendants(Dav + "prop");
                var href = r.Element(Dav + "href")?.Value.TrimEnd('/') ?? "";
                var comps = prop.Elements(Cal + "supported-calendar-component-set").Elements(Cal + "comp").Select(c => (string?)c.Attribute("name")).ToList();
                return (Href: href, Id: href[(href.LastIndexOf('/') + 1)..],
                    Name: prop.Elements(Dav + "displayname").FirstOrDefault()?.Value is { Length: > 0 } n ? n : Uri.UnescapeDataString(href[(href.LastIndexOf('/') + 1)..]),
                    IsTaskList: prop.Elements(Dav + "resourcetype").Elements(Cal + "calendar").Any() && (comps.Count == 0 || comps.Contains("VTODO")));
            })
            .Where(l => l.IsTaskList && (a.Lists.Length == 0 || a.Lists.Any(x => x.Equals(l.Name, StringComparison.OrdinalIgnoreCase) || x.Equals(Uri.UnescapeDataString(l.Id), StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (lists.Count == 0) throw new InvalidOperationException(a.Lists.Length == 0 ? "No task lists in this Nextcloud account" : "None of the chosen task lists was found");

        var tasks = new List<NcTask>();
        foreach (var l in lists)
        {
            var doc = await DavAsync(a, "REPORT", new Uri(a.Base, l.Href + "/"),
                "<c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:calendar-data/></d:prop>"
                + "<c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VTODO\"><c:prop-filter name=\"COMPLETED\"><c:is-not-defined/></c:prop-filter></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>", ct);
            foreach (var r in doc.Descendants(Dav + "response"))
            {
                var href = r.Element(Dav + "href")?.Value ?? "";
                var file = href[(href.LastIndexOf('/') + 1)..];
                if (file.Length == 0 || r.Descendants(Cal + "calendar-data").FirstOrDefault()?.Value is not { } ics) continue;
                tasks.AddRange(Ics.ParseTodos(ics).Select(t => new NcTask(TaskId(l.Id, file), t.Title, l.Name, t.Due, t.AllDay, t.Priority, l.Id, file)));
            }
        }
        // due first (soonest first), then by priority (1 high … 9 low, none last), then by title
        return tasks.OrderBy(t => t.Due ?? long.MaxValue).ThenBy(t => t.Priority == 0 ? 10 : t.Priority)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).Take(MaxTasks).ToList();
    }

    internal static string TaskId(string calendar, string file) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(calendar + "/" + file)))[..16].ToLowerInvariant();

    private static async Task<XDocument> DavAsync(NcAccount a, string method, Uri uri, string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), uri) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
        req.Headers.Add("Depth", "1");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(a.User + ":" + a.Password)));
        using var res = await Http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("Nextcloud refused the login: check the user and the app password");
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Nextcloud answered {(int)res.StatusCode} {res.ReasonPhrase}");
        return XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }
}
