using AgentBridge.UIAutomation.Locators;
using Xunit;

namespace AgentBridge.UIAutomation.Tests;

public sealed class ElementSemanticsTests
{
    [Theory]
    [InlineData("Agent Bridge", "title-button", "Agent Bridge")]
    [InlineData("PR #22, rename session", "title-button", "PR #22")]
    [InlineData("  Agent   Bridge ", "title-button", "Agent Bridge")]
    public void CurrentConversationMarker_AcceptsVerifiedHeaderForms(string name, string css, string identifier) =>
        Assert.True(ElementSemantics.IsCurrentConversationMarker("Button", name, css, false, identifier));

    [Theory]
    [InlineData("Agent Bridge", "sidebar-item", "Agent Bridge")]
    [InlineData("Agent Bridge", "group/folder-row", "Agent Bridge")]
    [InlineData("Another chat", "title-button", "Agent Bridge")]
    [InlineData("Agent Bridge", "title-button", "")]
    public void CurrentConversationMarker_RejectsAmbiguousOrWrongElements(string name, string css, string identifier) =>
        Assert.False(ElementSemantics.IsCurrentConversationMarker("Button", name, css, false, identifier));

    [Fact]
    public void CurrentConversationMarker_RejectsNonButtonWithMatchingName() =>
        Assert.False(ElementSemantics.IsCurrentConversationMarker("Group", "Agent Bridge", "group/cwd", false, "Agent Bridge"));

    [Theory]
    // A Claude project header carries the project name and expands to reveal the
    // sessions filed under it. Accepting it would let an unrelated open session
    // pass as the configured conversation.
    [InlineData("RASTA", "hide-focus-ring group/label", "RASTA")]
    [InlineData("Agent Bridge", "df-pill hide-focus-ring", "Agent Bridge")]
    public void CurrentConversationMarker_RejectsExpandableContainerCarryingTheSameName(
        string name,
        string css,
        string identifier) =>
        Assert.False(ElementSemantics.IsCurrentConversationMarker("Button", name, css, true, identifier));

    [Fact]
    public void CurrentConversationMarker_StillAcceptsTheRenameSessionTitleClaudeExposes() =>
        Assert.True(ElementSemantics.IsCurrentConversationMarker(
            "Button", "RASTA, rename session", "truncate text-body-medium text-primary", false, "RASTA"));

    [Theory]
    [InlineData("Button", "RASTA, rename session", "RASTA", true)]
    [InlineData("Button", "RASTA", "RASTA", false)]
    [InlineData("Button", "Other, rename session", "RASTA", false)]
    [InlineData("Group", "RASTA, rename session", "RASTA", false)]
    public void PreferredCurrentConversationMarker_RequiresExactRenameSessionHeader(
        string type, string name, string identifier, bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsPreferredCurrentConversationMarker(type, name, identifier));

    [Theory]
    // ChatGPT rows carry the thread title verbatim.
    [InlineData("Button", "RASTA Project", "sidebar-item selected", "RASTA Project", true)]
    [InlineData("Button", "RASTA Project", "title-button", "RASTA Project", false)]
    [InlineData("Button", "RASTA Project Docs", "sidebar-item", "RASTA Project", false)]
    [InlineData("Group", "RASTA Project", "sidebar-item", "RASTA Project", false)]
    [InlineData("Button", "RASTA Project", "sidebar-item folder-row", "RASTA Project", false)]
    public void ConversationNavigationCandidate_MatchesChatGptRowsExactly(
        string type, string name, string css, string identifier, bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsConversationNavigationCandidate(type, name, css, false, identifier));

    [Theory]
    // Claude prefixes its rows with a status badge, so the title is a suffix.
    [InlineData("#25 · Open RASTA Bridge", "RASTA Bridge", true)]
    [InlineData("Running RASTA Bridge", "RASTA Bridge", true)]
    [InlineData("RASTA Bridge", "RASTA Bridge", true)]
    // "AgentBridge" must not answer to "Bridge": the title has to start on a word
    // boundary, otherwise an unrelated session would be opened confidently.
    [InlineData("Running AgentBridge", "Bridge", false)]
    [InlineData("Running AgentBridge", "RASTA Bridge", false)]
    // The title is a suffix, never a prefix or an interior match.
    [InlineData("RASTA Bridge notes", "RASTA Bridge", false)]
    public void ConversationNavigationCandidate_MatchesClaudeRowsAsWholeWordSuffix(
        string name, string identifier, bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsConversationNavigationCandidate(
            "Button", name, "w-full text-[length:var(--df-row-font)] px-[var(--df-row-px)]", false, identifier));

    [Fact]
    public void ConversationNavigationCandidate_RejectsExpandableSectionHeaders() =>
        Assert.False(ElementSemantics.IsConversationNavigationCandidate(
            "Button", "More navigation items", "text-[length:var(--df-row-font)]", true, "More navigation items"));

    [Theory]
    [InlineData("Edit", "Prompt", "tiptap ProseMirror")]
    [InlineData("Edit", "Do anything", "ProseMirror")]
    [InlineData("Edit", "Localized label", "editor ProseMirror")]
    public void InputCandidate_AcceptsKnownSemanticEditors(string type, string name, string css) =>
        Assert.True(ElementSemantics.IsInputCandidate(type, name, css));

    [Theory]
    [InlineData("Document", "Prompt", "ProseMirror")]
    [InlineData("Edit", "Search", "search-input")]
    public void InputCandidate_RejectsUnrelatedControls(string type, string name, string css) =>
        Assert.False(ElementSemantics.IsInputCandidate(type, name, css));

    [Theory]
    [InlineData("Button", "Send", true)]
    [InlineData("Button", "Stop", false)]
    [InlineData("Edit", "Send", false)]
    public void SendButton_RequiresExactButtonSemantics(string type, string name, bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsSendButton(type, name));

    [Theory]
    [InlineData("Button", "Stop", true)]
    [InlineData("Button", "Send", false)]
    [InlineData("Text", "Stop", false)]
    public void ProcessingButton_RequiresExactStopButton(string type, string name, bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsProcessingButton(type, name));

    [Theory]
    [InlineData("Session limit reached", true)]
    [InlineData(" session   limit reached ", true)]
    [InlineData("Claude finished the response", false)]
    public void QuotaLimitMarker_RecognizesCurrentLimitStatus(string name, bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsQuotaLimitMarker(name));

    [Theory]
    [InlineData("Resets in 22 min", true)]
    [InlineData("Resets in 3 hr 52 min", true)]
    [InlineData("Usage: 100%", false)]
    public void QuotaResetMarker_RecognizesCountdown(string name, bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsQuotaResetMarker(name));

    [Theory]
    [InlineData("Complete the task", "Complete the task", true)]
    [InlineData("You said: Complete the task", "Complete the task", true)]
    [InlineData("You said: prefixComplete the task", "Complete the task", false)]
    [InlineData("Complete only part", "Complete the task", false)]
    public void RenderedReceipt_RequiresExactMessageOrExactAccessibilityPrefix(
        string rendered,
        string message,
        bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsExactRenderedReceipt(rendered, message));

    [Theory]
    [InlineData("Type / for commands", true)]
    [InlineData("Do anything", true)]
    [InlineData("Prompt", true)]
    [InlineData("User-authored draft", false)]
    public void EditorPlaceholder_DistinguishesAccessiblePlaceholderFromDraft(string value, bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsEditorPlaceholder(value));

    [Theory]
    [InlineData("Document", "RootWebArea", true)]
    [InlineData("document", "RootWebArea", true)]
    [InlineData("Pane", "RootWebArea", false)]
    [InlineData("Document", "", false)]
    [InlineData("Button", "", false)]
    public void RendererDocumentRoot_IsTheWarmedAccessibilityTreeSignal(
        string controlType,
        string automationId,
        bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsRendererDocumentRoot(controlType, automationId));
}
