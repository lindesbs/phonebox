using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public class NextcloudTasksTests
{
    private static WidgetConfig Widget(object options) => new()
    {
        Type = NextcloudTasks.WidgetType,
        Options = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(options)),
    };

    [Fact]
    public void OpenTodosOnlyWithDueAndPriority()
    {
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nUID:a\r\nSUMMARY:Pay\\, rent\r\nDUE;VALUE=DATE:20261201\r\nPRIORITY:1\r\n"
            + "BEGIN:VALARM\r\nSUMMARY:alarm\r\nEND:VALARM\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:b\r\nSUMMARY:Done\r\nSTATUS:COMPLETED\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:c\r\nSUMMARY:Later\r\nDUE:20261202T100000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var todos = Ics.ParseTodos(ics).ToList();
        Assert.Equal(2, todos.Count);
        Assert.Equal(("Pay, rent", new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), true, 1), todos[0]);
        Assert.Equal(("Later", new DateTimeOffset(2026, 12, 2, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), false, 0), todos[1]);
    }

    [Fact]
    public void AccountNeverReachesThePhone()
    {
        var w = Widget(new { title = "Tasks", url = "https://cloud.example.com", user = "me", password = "secret", lists = new[] { "Work" }, open = true });
        var pub = StackWidgets.PublicOptions(w)!;
        Assert.Equal(["open", "title"], pub.Keys.Order());
        var stack = new WidgetConfig { Type = "stack", Options = new() { ["widgets"] = JsonSerializer.SerializeToElement(new[] { new { id = "c", type = w.Type, options = w.Options } }) } };
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(StackWidgets.PublicOptions(stack)));
    }

    [Fact]
    public void AccountAndTaskLink()
    {
        Assert.Null(NextcloudTasks.Account(Widget(new { url = "ftp://x", user = "me", password = "p" })));
        Assert.Null(NextcloudTasks.Account(Widget(new { url = "https://cloud.example.com" })));
        var a = NextcloudTasks.Account(Widget(new { url = "https://cloud.example.com/nc/", user = "me", password = "p", lists = new[] { " Work ", "" } }))!;
        Assert.Equal(["Work"], a.Lists);
        Assert.Equal("https://cloud.example.com/nc/apps/tasks/calendars/personal/tasks/ABC%20D.ics",
            NextcloudTasks.TaskUri(a.Base, "personal", "ABC%20D.ics").AbsoluteUri);
    }
}
