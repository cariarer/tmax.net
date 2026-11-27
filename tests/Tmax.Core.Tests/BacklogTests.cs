using Tmax.Core.Backlog;

namespace Tmax.Core.Tests;

public sealed class BacklogTests : IDisposable
{
    private readonly string _project = Directory.CreateTempSubdirectory("tmax-backlog-").FullName;

    public void Dispose() => TestDirs.Delete(_project);

    private string TasksDir => Path.Combine(_project, "backlog", "tasks");

    [Theory] // ported from tests/unit/main/backlog-writer.test.ts
    [InlineData("TASK-42", "42")]
    [InlineData("task-7", "7")]
    [InlineData("task-12 - Foo.md", "12")]
    [InlineData("abc", "abc")]
    public void Bare_id(string input, string expected) => Assert.Equal(expected, BacklogWriter.BareId(input));

    [Theory]
    [InlineData("Plain title", "Plain title")]
    [InlineData("Fix: crash", "'Fix: crash'")]
    [InlineData("it's #1", "'it''s #1'")]
    [InlineData("- dash", "'- dash'")]
    [InlineData("yes", "'yes'")]
    [InlineData("1.5", "'1.5'")]
    [InlineData("", "''")]
    public void Yaml_scalars_are_quoted_only_when_needed(string input, string expected) =>
        Assert.Equal(expected, BacklogWriter.YamlScalar(input));

    [Fact]
    public void Frontmatter_parser_handles_lists_block_scalars_and_quotes()
    {
        var fm = Frontmatter.Parse("""
            ---
            id: TASK-3
            title: >-
              A long title
              folded here
            status: 'In Progress'
            assignee:
              - '@me'
            labels: []
            notes: |
              line 1
              line 2
            ---
            body
            """.Replace("\r\n", "\n"))!;

        Assert.Equal("A long title folded here", fm["title"]);
        Assert.Equal("In Progress", fm["status"]);
        Assert.Equal(["@me"], (List<string>)fm["assignee"]);
        Assert.Empty((List<string>)fm["labels"]);
        Assert.Equal("line 1\nline 2", fm["notes"]);
    }

    [Fact]
    public async Task Create_edit_list_and_archive_round_trip()
    {
        Assert.True(BacklogWriter.InitProject(_project, "Demo").Ok);
        var created = BacklogWriter.Create(new CreatePayload(_project, "First task: setup", Description: "Initial", Labels: ["ui"]));
        Assert.Equal("TASK-1", created.Id);
        Assert.Equal("TASK-2", BacklogWriter.Create(new CreatePayload(_project, "Second")).Id);

        var file = Directory.GetFiles(TasksDir, "task-1 - *.md").Single();
        Assert.EndsWith("task-1 - First-task-setup.md", file);
        var text = File.ReadAllText(file);
        Assert.Contains("title: 'First task: setup'", text);
        Assert.Contains("  - ui", text);

        // Add an AC block the CLI would write, then check #2 and retitle.
        File.AppendAllText(file, "\n## Acceptance Criteria\n<!-- AC:BEGIN -->\n- [ ] #1 one\n- [ ] #2 two\n<!-- AC:END -->\n");
        var edit = BacklogWriter.Edit(new EditPayload(_project, "TASK-1", Status: "In Progress", Title: "Renamed", Description: "New desc", CheckAc: [2]));
        Assert.True(edit.Ok, edit.Error);

        var renamed = Path.Combine(TasksDir, "task-1 - Renamed.md");
        var edited = File.ReadAllText(renamed);
        Assert.Contains("status: In Progress", edited);
        Assert.Contains("- [ ] #1 one", edited);
        Assert.Contains("- [x] #2 two", edited);
        Assert.Contains("<!-- SECTION:DESCRIPTION:BEGIN -->\nNew desc\n<!-- SECTION:DESCRIPTION:END -->", edited);
        Assert.Matches(@"created_date: '[^']+'\nupdated_date: '", edited);

        var tasks = await BacklogService.ListAllAsync([new BacklogProjectRef("Demo", _project)], includeArchived: false);
        Assert.Contains(tasks, t => t is { Id: "TASK-1", Title: "Renamed", Status: "In Progress" });

        Assert.True(BacklogWriter.Archive(_project, "TASK-2").Ok);
        var withArchive = await BacklogService.ListAllAsync([new BacklogProjectRef("Demo", _project)], includeArchived: true);
        Assert.Contains(withArchive, t => t is { Id: "TASK-2", Status: "Archived" });

        // Un-archive by setting a real status.
        Assert.True(BacklogWriter.Edit(new EditPayload(_project, "TASK-2", Status: "To Do")).Ok);
        Assert.Single(Directory.GetFiles(TasksDir, "task-2 - *.md"));
    }

    [Fact]
    public void Crlf_files_keep_their_line_endings()
    {
        Directory.CreateDirectory(TasksDir);
        var file = Path.Combine(TasksDir, "task-5 - X.md");
        File.WriteAllText(file, "---\r\nid: TASK-5\r\ntitle: X\r\nstatus: To Do\r\ncreated_date: '2026-01-01 10:00'\r\n---\r\n\r\nBody\r\n");

        Assert.True(BacklogWriter.Edit(new EditPayload(_project, "5", Status: "Done")).Ok);

        var text = File.ReadAllText(file);
        Assert.Contains("status: Done\r\n", text);
        Assert.DoesNotContain("\r\r", text);
        Assert.DoesNotMatch("[^\r]\n", text);
    }

    [Fact]
    public void Ids_are_allocated_from_disk_and_zero_padded()
    {
        BacklogWriter.InitProject(_project, "P");
        File.AppendAllText(Path.Combine(_project, "backlog", "config.yml"), "zero_padded_ids: 3\n");
        Directory.CreateDirectory(Path.Combine(_project, "backlog", "archive", "tasks"));
        File.WriteAllText(Path.Combine(_project, "backlog", "archive", "tasks", "task-41 - Old.md"), "---\nid: TASK-41\n---\n");

        Assert.Equal("TASK-042", BacklogWriter.Create(new CreatePayload(_project, "New")).Id);
    }

    [Theory]
    [InlineData("tasks", "../../etc/passwd")]
    [InlineData("../x", "a.md")]
    public void Get_task_refuses_traversal(string sub, string file) => Assert.Null(BacklogService.GetTask(_project, sub, file));
}
