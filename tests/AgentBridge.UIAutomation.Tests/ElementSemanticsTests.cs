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
        Assert.True(ElementSemantics.IsCurrentConversationMarker("Button", name, css, identifier));

    [Theory]
    [InlineData("Agent Bridge", "sidebar-item", "Agent Bridge")]
    [InlineData("Agent Bridge", "group/folder-row", "Agent Bridge")]
    [InlineData("Another chat", "title-button", "Agent Bridge")]
    [InlineData("Agent Bridge", "title-button", "")]
    public void CurrentConversationMarker_RejectsAmbiguousOrWrongElements(string name, string css, string identifier) =>
        Assert.False(ElementSemantics.IsCurrentConversationMarker("Button", name, css, identifier));

    [Fact]
    public void CurrentConversationMarker_RejectsNonButtonWithMatchingName() =>
        Assert.False(ElementSemantics.IsCurrentConversationMarker("Group", "Agent Bridge", "group/cwd", "Agent Bridge"));

    [Theory]
    [InlineData("Button", "RASTA, rename session", "RASTA", true)]
    [InlineData("Button", "RASTA", "RASTA", false)]
    [InlineData("Button", "Other, rename session", "RASTA", false)]
    [InlineData("Group", "RASTA, rename session", "RASTA", false)]
    public void PreferredCurrentConversationMarker_RequiresExactRenameSessionHeader(
        string type, string name, string identifier, bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsPreferredCurrentConversationMarker(type, name, identifier));

    [Theory]
    [InlineData("Button", "RASTA Project", "sidebar-item selected", "RASTA Project", true)]
    [InlineData("Button", "RASTA Project", "title-button", "RASTA Project", false)]
    [InlineData("Button", "RASTA Project Docs", "sidebar-item", "RASTA Project", false)]
    [InlineData("Group", "RASTA Project", "sidebar-item", "RASTA Project", false)]
    [InlineData("Button", "RASTA Project", "sidebar-item folder-row", "RASTA Project", false)]
    public void ConversationNavigationCandidate_RequiresUniqueExactSidebarButton(
        string type, string name, string css, string identifier, bool expected) =>
        Assert.Equal(expected, ElementSemantics.IsConversationNavigationCandidate(type, name, css, identifier));

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
