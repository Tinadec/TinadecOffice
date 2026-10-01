namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Model-facing declarations of the organization tools and <c>graph_view</c>, in one place because
/// three modules hand them to a model: the tool manifest freezes them into a run, the communication
/// module offers them to a standing member's turn, and the member runner adds <c>graph_view</c>. One
/// table means the description a worker reads and the one a reviewer reads can never drift apart.
///
/// Addressing is by the name members see (<c>search#1</c>, <c>reviewer</c>, <c>meeting</c>), never
/// by an internal id: a model reliably repeats a short name it was shown and garbles a UUID.
/// </summary>
public static class OrganizationToolCatalog
{
    public sealed record Entry(string Id, string Description, string SchemaJson, bool Writes);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("org_directory",
            "See who is in this session's organization and which rooms you can read: every member's address (the name to use in other org_* calls), "
            + "role (human, conversation, governance, executor), whether it is online, and whether it is already your contact; plus pending contact requests. "
            + "Executors that finished stay listed as offline so their messages remain attributable.",
            Obj(("role", Enum("Only members with this role.", "human", "conversation", "governance", "executor")),
                ("query", Str("Optional substring of an address or agent name."))),
            Writes: false),

        new("org_read",
            "Read one place in the organization, oldest first, from a cursor: a room by room_id, 'lobby', 'board', 'plan' (your plan room), "
            + "'inbox' (everything addressed to you), or a direct chat by passing with=<address>. Pass the returned next_cursor as after_sequence to continue; "
            + "nothing new arrives inside a call. kinds narrows to message, report or notice.",
            Obj(("room", Str("room_id from org_directory, or one of: lobby, board, plan, inbox. Omit when using with.")),
                ("with", Str("Address of a member to read your direct chat with.")),
                ("after_sequence", Int("Cursor from a previous page; omit to start from the beginning.")),
                ("kinds", ArrEnum("Only these message kinds.", "message", "report", "notice"))),
            Writes: false),

        new("org_send",
            "Post a message. Either into a room you are a member of (room_id, 'lobby', 'plan', or 'board' if your role may post there), "
            + "or directly to one member with to=<address> — a direct message needs the member to be your contact (the people who dispatched you, "
            + "your siblings, governance and the user always are; for anyone else use org_contact first). Returns at once; a reply, if any, is a later "
            + "turn of somebody else, so never report an answer you have not read with org_read.",
            Obj(("room", Str("room_id, or lobby, plan or board. Omit when using to.")),
                ("to", Str("Address of one member for a direct message.")),
                ("content", Str("The message, in your own voice. State facts you verified; say what you want from the reader.")),
                ("mention", ArrStr("Room members who must see this in their inbox (a governance member is also woken by it). Everyone else reads the room when they choose.")),
                ("reply_to_message_id", Str("Optional message_id you are answering."))),
            Writes: true),

        new("org_report",
            "File a report: your observation and judgement about one part of the work, as a structured post. A report changes nothing by itself — "
            + "the proposed action is carried out, if at all, by whoever holds that verb, and they record the decision. Point subject at the thing you "
            + "mean (a run, task, lease, approval, member or path id from graph_view) so readers see its live state instead of your sentence, and give "
            + "evidence that can be checked (event sequence, lease id, file:line), not a retelling.",
            Obj(("room", Str("Where to post it: a room_id, lobby (default) or plan.")),
                ("kind", Enum("What the report is about.", "conflict", "risk", "drift", "budget", "progress")),
                ("severity", Enum("info = for the record, warning = look soon, blocking = work should not continue as is.", "info", "warning", "blocking")),
                ("subject_kind", Enum("What subject_id names.", "run", "task", "lease", "approval", "member", "path")),
                ("subject_id", Str("The id (or path) of the subject, copied from graph_view.")),
                ("finding", Str("What you observed and what you conclude, in two or three sentences.")),
                ("evidence", ArrStr("Checkable references: event sequence numbers, lease ids, file:line, command output lines.")),
                ("proposed_verb", Enum("The one action you recommend, if any.", "none", "wait", "serialize", "reassign", "separate_worktree", "narrow_scope", "pause_run", "resume_run", "stop_run", "assign_environment", "ask_user")),
                ("proposed_args", Str("Optional action arguments. For assign_environment prefer JSON such as {key: staging, reason: run tests} (JSON string) or key=value;key=value.")),
                ("supersedes_report_id", Str("Optional report_id this one replaces; the old one is marked superseded."))),
            Writes: true),

        new("org_decide_report",
            "Record what happened to a report: acted (its proposal was carried out, by you or someone holding the verb) or dismissed (with the reason). "
            + "Pass the revision exactly as org_read returned it — a stale revision fails instead of overwriting a newer decision. "
            + "Only the user, the conversation identity and the report's own author may decide.",
            Obj(("report_id", Str("report_id from org_read.")),
                ("decision", Enum("The outcome.", "acted", "dismissed")),
                ("note", Str("What was done, or why it was dismissed.")),
                ("expected_revision", Int("The report's revision as you read it."))),
            Writes: true),

        new("org_execute_report",
            "Execute the open report's proposed governance action through the runtime action executor, then record acted only after the operation is accepted. "
            + "This is the only organization tool that changes a run because of a report. Read the report first and pass its exact revision; a stale revision or a target outside this session fails closed. "
            + "Currently supported actions are pause_run, resume_run, stop_run (stop_run maps to durable cancel), assign_environment and separate_worktree. wait and ask_user leave the report open; unsupported actions also stay open with a reason.",
            Obj(("report_id", Str("report_id from org_read.")),
                ("expected_revision", Int("The report revision as org_read returned it."))),
            Writes: true),

        new("org_contact",
            "Manage contacts: request someone who is not yet your contact (they must accept before you can message them directly), "
            + "or accept / decline a request you received (see org_directory).",
            Obj(("action", Enum("What to do.", "request", "accept", "decline")),
                ("address", Str("The other member's address."))),
            Writes: true),

        new("org_room",
            "Open an ad-hoc room with some of your contacts, invite a contact into a room you administer, or leave a room. "
            + "Rooms hold at most 64 members; for one person use org_send to=<address> instead.",
            Obj(("action", Enum("What to do.", "create", "invite", "leave")),
                ("title", Str("Title of a new room.")),
                ("members", ArrStr("Addresses to include (create) or invite (invite).")),
                ("room_id", Str("The room to invite into or leave."))),
            Writes: true),

        new(CoreVirtualToolPolicy.GraphViewToolId,
            "See the whole session as a graph: its runs (status, phase), each run's tasks (status, which worker holds it, who dispatched it, declared "
            + "write_scope), the instances working them, the resources currently leased (path, worktree; purpose and holder), and the organization's "
            + "members. Read-only and bounded — lists say when they were cut. Use the ids it returns as report subjects.",
            Obj(("run_id", Str("Optional: focus on one run.")),
                ("include_finished", Bool("Include runs that already ended (default true)."))),
            Writes: false),

        new(CoreVirtualToolPolicy.RecallEvidenceToolId,
            "Search this session's evidence archive: the full text of every finished task's result, every governance report, what standing "
            + "members concluded, and the context summaries — kept verbatim, never compressed. Use it to go back from a summary to what was actually "
            + "said, or to find whether something was already tried. Semantic when an embedding model is configured, keyword otherwise; the result "
            + "says which, and each hit names its run, task and author so you can cite it.",
            Obj(("query", Str("What to look for: keywords, a path, a handle like search#1, or a sentence.")),
                ("kinds", ArrEnum("Only these kinds of evidence.", EvidenceKinds.TaskResult, EvidenceKinds.Report, EvidenceKinds.MemberTurn, EvidenceKinds.Summary)),
                ("run_id", Str("Optional: only evidence from this run (an id from graph_view).")),
                ("limit", Int("How many hits (default 8, at most 20)."))),
            Writes: false),

        new(CoreVirtualToolPolicy.EnvironmentListToolId,
            "List the environments registered in this workspace — test, cloud, remote, local and terminal — with how to reach each "
            + "(connection; credentials are named by secret_ref, never shown), its capacity, and which runs hold its slots right now.",
            Obj(("kind", Enum("Only environments of this kind.", EnvironmentKinds.Local, EnvironmentKinds.Cloud, EnvironmentKinds.Remote, EnvironmentKinds.Terminal, EnvironmentKinds.Test))),
            Writes: false),

        new(CoreVirtualToolPolicy.EnvironmentAcquireToolId,
            "Take one environment for this run: name it (key) or ask for any available one of a kind. Granted, the slot is this run's until you "
            + "release it or the run ends; refused, the result says who holds what you asked for — wait, pick another, or report it. "
            + "Asking again for one you already hold returns the same slot.",
            Obj(("key", Str("The environment to take, as environment_list names it.")),
                ("kind", Enum("Any available environment of this kind (when no key is given).", EnvironmentKinds.Local, EnvironmentKinds.Cloud, EnvironmentKinds.Remote, EnvironmentKinds.Terminal, EnvironmentKinds.Test)),
                ("reason", Str("What you need it for, shown to whoever else wants it."))),
            Writes: true),

        new(CoreVirtualToolPolicy.EnvironmentReleaseToolId,
            "Give back an environment this run holds, as soon as you no longer need it, so another run can use it.",
            Obj(("key", Str("The environment to release."))),
            Writes: true),
    ];

    public static Entry? Find(string? toolId) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Id, toolId, StringComparison.OrdinalIgnoreCase));

    private static string Obj(params (string Name, string Schema)[] fields) =>
        "{\"type\":\"object\",\"properties\":{" + string.Join(",", fields.Select(x => $"\"{x.Name}\":{x.Schema}")) + "},\"additionalProperties\":false}";

    private static string Str(string description) => "{\"type\":\"string\",\"description\":\"" + Escape(description) + "\"}";

    private static string Int(string description) => "{\"type\":\"integer\",\"description\":\"" + Escape(description) + "\"}";

    private static string Bool(string description) => "{\"type\":\"boolean\",\"description\":\"" + Escape(description) + "\"}";

    private static string ArrStr(string description) =>
        "{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"" + Escape(description) + "\"}";

    private static string Enum(string description, params string[] values) =>
        "{\"type\":\"string\",\"enum\":[" + string.Join(",", values.Select(v => $"\"{v}\"")) + "],\"description\":\"" + Escape(description) + "\"}";

    private static string ArrEnum(string description, params string[] values) =>
        "{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[" + string.Join(",", values.Select(v => $"\"{v}\"")) + "]},\"description\":\"" + Escape(description) + "\"}";

    private static string Escape(string value) => value.Replace("\\", "").Replace("\"", "'");
}
