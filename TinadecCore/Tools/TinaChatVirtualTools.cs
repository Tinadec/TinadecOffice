using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Manifest entries for the TinaChat tools. Like <c>create_workspace</c> and <c>task_dispatch</c>
/// these never reach a tool child process: Core executes them against its own communication state,
/// so they must be injected into the frozen manifest when a mode declares them, or the agent would
/// hold a declaration it can never see.
///
/// No approval gate: these tools only write communication records that TinaChat's own audience,
/// provenance and membership rules already bound, and the acting identity is the run's own
/// principal. Approval would gate a chat message the same way a workspace write is gated and would
/// make an agent that cannot wait simply stop talking.
/// </summary>
internal static class TinaChatVirtualTools
{
    private static readonly (string Id, string Description, string Schema)[] Entries =
    [
        ("tina_chat_bind",
         "Speak in the chatroom as one of your own registered handles. Required before reading or sending anything. "
         + "Only a handle the authenticated principal owns can be bound, and a session can bind only one.",
         Obj(("handle", Str("The handle you own, e.g. 'atlas'. Find it with tina_chat_search_people.")))),

        ("tina_chat_search_people",
         "Look up participants you may address: handle, display name, kind and whether they receive human originals. "
         + "Handles, not ids, are how you address people.",
         Obj(("query", Str("Optional substring matched against handle, display name or job title.")))),

        ("tina_chat_list_rooms",
         "List the conversations your bound identity belongs to, with the conversation_id you need to read or post in one.",
         Obj()),

        ("tina_chat_read_inbox",
         "Read messages addressed to your bound identity, oldest first, from a durable inbox. This is a pull: nothing "
         + "awaits you inside a call. Use the returned next_cursor as after_sequence to continue.",
         Obj(("after_sequence", Str("Optional cursor from a previous page; 0 or omitted reads from your first unread point.")))),

        ("tina_chat_send",
         "Post one message into a conversation you are an active member of. Returns immediately; any reply is a later turn "
         + "for somebody else and never arrives in this call, so do not report an answer you have not read. "
         + "audience_addresses may name only current members; omit it to address the room's active members. Confidential "
         + "content never crosses workspaces, and setting allow_derived_sharing is what lets an interpreter summarise it.",
         Obj(
            ("conversation_id", Str("conversation_id from tina_chat_list_rooms.")),
            ("content", Str("The message text, in your own voice.")),
            ("audience_handles", ArrStr("Handles who should receive this; omit for all active members.")),
            ("reply_to_message_id", Str("Optional message id you are answering.")),
            ("source_message_ids", ArrStr("Optional message ids this message quotes; each must be readable by you and by every recipient.")),
            ("allow_derived_sharing", Bool("Allow an interpreter to reuse this message in a brief without republishing the original.")),
            ("sensitivity", Enum("normal", "confidential")))),

        ("tina_chat_propose_intent",
         "File a structured brief built only from messages you may read, for an owner or admin to accept. Mark anything the "
         + "user merely claimed under user_statements rather than as fact, keep guesses in assumptions, and put anything that "
         + "must be answered before work can start in blocking_questions - a brief with blocking questions cannot be executed.",
         Obj(
            ("conversation_id", Str("conversation_id from tina_chat_list_rooms.")),
            ("source_message_ids", ArrStr("Message ids the brief is built from; at least one, all readable by you.")),
            ("goal", Str("The outcome being asked for.")),
            ("user_statements", ArrStr("What users actually said, as claims awaiting verification.")),
            ("constraints", ArrStr("Limits the user or the room imposed.")),
            ("assumptions", ArrStr("What you assumed and did not verify.")),
            ("open_questions", ArrStr("Worth clarifying but not blocking.")),
            ("blocking_questions", ArrStr("Must be answered before execution; non-empty blocks the handoff.")),
            ("acceptance_criteria", ArrStr("How the finished work is recognised.")),
            ("audience_handles", ArrStr("Handles who may see the brief; omit for the members entitled to your sources.")))),

        ("tina_chat_list_intents",
         "Read the briefs filed in a conversation, with the conversation_revision you must pass to decide one. Statuses: "
         + "proposed awaits an owner or admin, accepted is the conversation's current authority (older ones read superseded), "
         + "rejected is closed. Reading is not deciding - a brief you authored still needs an accept from somebody with the role.",
         Obj(("conversation_id", Str("conversation_id from tina_chat_list_rooms.")))),

        ("tina_chat_decide_intent",
         "Accept or reject a brief as the conversation role your bound identity holds. This is the step that turns chat into an "
         + "authorised goal: ordinary messages are never a confirmed goal change. Pass the conversation_revision exactly as "
         + "tina_chat_list_intents returned it - a stale revision fails instead of overwriting a newer decision. Only an owner "
         + "or admin of that conversation is answered with anything other than a refusal.",
         Obj(
            ("conversation_id", Str("conversation_id from tina_chat_list_rooms.")),
            ("intent_id", Str("intent_id from tina_chat_list_intents.")),
            ("decision", Enum("accepted", "rejected")),
            ("expected_revision", Num("conversation_revision read from tina_chat_list_intents, for compare-and-set.")))),

        ("tina_chat_execute_intent",
         "Hand the accepted brief to your bound identity for isolated execution: Core opens the durable session bound to that "
         + "brief and starts a run whose only input is the brief itself - the raw room, unrelated session history and long-term "
         + "memory are not read. Omit mode_version_id to use the workspace's published default mode. The run is detached: this "
         + "call returns its identifiers, not its answer, and the outcome is posted back to the room when it terminates.",
         Obj(
            ("conversation_id", Str("conversation_id from tina_chat_list_rooms.")),
            ("intent_id", Str("intent_id of the ACCEPTED brief - proposing or deciding is not enough.")),
            ("mode_version_id", Str("Optional published mode version to run under; omit for the workspace default.")))),
    ];

    public static bool IsCoreTool(string? toolId) => CoreVirtualToolPolicy.IsTinaChat(toolId);

    public static ToolManifestEntryDto? ManifestEntry(string? toolId)
    {
        var found = Entries.FirstOrDefault(x => string.Equals(x.Id, toolId, StringComparison.OrdinalIgnoreCase));
        if (found.Id is null) return null;
        return new ToolManifestEntryDto
        {
            Id = found.Id,
            Description = found.Description,
            RequiresApproval = false,
            InputSchema = JsonDocument.Parse(found.Schema).RootElement.Clone(),
            Risk = "low",
            MutatesWorkspace = false,
            // Every write keys on the model's own tool-call id, so replaying this call returns the
            // original record instead of a second message or brief.
            RetrySafety = "safe",
            ConfirmationFields = [],
        };
    }

    private static string Obj(params (string Name, string Schema)[] fields) =>
        "{\"type\":\"object\",\"properties\":{" + string.Join(",", fields.Select(x => $"\"{x.Name}\":{x.Schema}")) + "},\"additionalProperties\":false}";

    private static string Str(string description) =>
        "{\"type\":\"string\",\"description\":\"" + Escape(description) + "\"}";

    private static string ArrStr(string description) =>
        "{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"" + Escape(description) + "\"}";

    private static string Bool(string description) =>
        "{\"type\":\"boolean\",\"description\":\"" + Escape(description) + "\"}";

    private static string Num(string description) =>
        "{\"type\":\"integer\",\"description\":\"" + Escape(description) + "\"}";

    private static string Enum(params string[] values) =>
        "{\"type\":\"string\",\"enum\":[" + string.Join(",", values.Select(v => $"\"{v}\"")) + "],\"description\":\"Defaults to the first value when omitted.\"}";

    private static string Escape(string value) => value.Replace("\\", "").Replace("\"", "'");
}
