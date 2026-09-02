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
}
