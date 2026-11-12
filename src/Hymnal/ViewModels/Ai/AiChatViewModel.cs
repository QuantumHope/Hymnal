using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Hymnal.Core.Infrastructure.Ai;
using Hymnal.Core.Interfaces;
using Hymnal.Core.Models;
using Hymnal.Core.Models.Ai;
using Hymnal.Core.Services.Ai;
using Microsoft.Extensions.AI;
using ReactiveUI;

namespace Hymnal.ViewModels.Ai;

/// <summary>
/// Main AI chat panel view-model.
/// Manages the active conversation, streaming, role/scope selection, and conversation persistence.
/// Maps ShellMode → appropriate IContextBuilder.
/// </summary>
public class AiChatViewModel : ViewModelBase, IDisposable
{
    private readonly IAiChatService _chatService;
    private readonly IConversationStore _conversationStore;
    private readonly EditorViewModel _editor;
    private readonly WorkspaceViewModel _workspace;
    private readonly INotificationService _notifications;
    private readonly IRolePromptProvider _roleProvider;
    private readonly ConversationListViewModel _conversationList;

    private readonly WriteContextBuilder _writeContextBuilder;
    private readonly PlanContextBuilder _planContextBuilder;
    private readonly ManageContextBuilder _manageContextBuilder;

    // ── State ────────────────────────────────────────────────────────────────

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    private bool _isListVisible;
    public bool IsListVisible
    {
        get => _isListVisible;
        set => this.RaiseAndSetIfChanged(ref _isListVisible, value);
    }

    private bool _isStreaming;
    public bool IsStreaming
    {
        get => _isStreaming;
        private set => this.RaiseAndSetIfChanged(ref _isStreaming, value);
    }

    private string _composerText = string.Empty;
    public string ComposerText
    {
        get => _composerText;
        set => this.RaiseAndSetIfChanged(ref _composerText, value);
    }

    private AiRole _selectedRole;
    public AiRole SelectedRole
    {
        get => _selectedRole;
        set => this.RaiseAndSetIfChanged(ref _selectedRole, value);
    }

    private ConversationScope _selectedScope = ConversationScope.Chapter;
    public ConversationScope SelectedScope
    {
        get => _selectedScope;
        set => this.RaiseAndSetIfChanged(ref _selectedScope, value);
    }

    private string? _conversationTitle;
    public string? ConversationTitle
    {
        get => _conversationTitle;
        private set => this.RaiseAndSetIfChanged(ref _conversationTitle, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    private bool _isProviderConfigured;
    public bool IsProviderConfigured
    {
        get => _isProviderConfigured;
        private set => this.RaiseAndSetIfChanged(ref _isProviderConfigured, value);
    }

    public bool HasWorkspace => !string.IsNullOrEmpty(_workspace.WorkspaceRoot);

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public IReadOnlyList<AiRole> AvailableRoles => _roleProvider.GetAvailableRoles(CurrentShellModeName);
    public ConversationListViewModel ConversationList => _conversationList;

    // ── Active conversation ──────────────────────────────────────────────────

    private Conversation? _activeConversation;
    private string? _activeConversationId;
    private CancellationTokenSource? _streamCts;

    // ── Commands ─────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> SendCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> NewConversationCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleListCommand { get; }
    public ReactiveCommand<Unit, Unit> TogglePanelCommand { get; }
    public ReactiveCommand<ChatMessageViewModel, Unit> RegenerateCommand { get; }
    public ReactiveCommand<ConversationScope, Unit> SetScopeCommand { get; }

    private readonly ObservableAsPropertyHelper<ProjectProfile> _profile;
    /// <summary>The open project's profile: the scope buttons and empty state use its words.</summary>
    public ProjectProfile Profile => _profile.Value;

    private readonly ObservableAsPropertyHelper<string> _emptyStateText;
    /// <summary>What an empty conversation says it will send as context.</summary>
    public string EmptyStateText => _emptyStateText.Value;

    // ── Constructor ──────────────────────────────────────────────────────────

    public AiChatViewModel(
        IAiChatService chatService,
        IConversationStore conversationStore,
        EditorViewModel editor,
        WorkspaceViewModel workspace,
        INotificationService notifications,
        IRolePromptProvider roleProvider,
        WriteContextBuilder writeContextBuilder,
        PlanContextBuilder planContextBuilder,
        ManageContextBuilder manageContextBuilder,
        ConversationListViewModel conversationList)
    {
        _chatService = chatService;
        _conversationStore = conversationStore;
        _editor = editor;
        _workspace = workspace;
        _notifications = notifications;
        _roleProvider = roleProvider;
        _writeContextBuilder = writeContextBuilder;
        _planContextBuilder = planContextBuilder;
        _manageContextBuilder = manageContextBuilder;
        _conversationList = conversationList;

        _profile = workspace.WhenAnyValue(x => x.Profile)
            .ToProperty(this, x => x.Profile, initialValue: workspace.Profile);
        _emptyStateText = workspace.WhenAnyValue(x => x.Profile)
            .Select(p => p.Kind switch
            {
                ProjectKind.Story => "Start a conversation. Your message is sent with the open scene as context.",
                ProjectKind.Anthology => "Start a conversation. Your message is sent with the open story or scene as context.",
                _ => "Start a conversation. Your message is sent with the current chapter as context."
            })
            .ToProperty(this, x => x.EmptyStateText);

        _selectedRole = _roleProvider.GetDefaultRole("Write");
        IsProviderConfigured = _chatService.IsProviderConfigured;

        var canSend = this.WhenAnyValue(
            x => x.ComposerText,
            x => x.IsStreaming,
            x => x.IsProviderConfigured,
            (text, streaming, configured) =>
                !string.IsNullOrWhiteSpace(text) && !streaming && configured);

        var canStop = this.WhenAnyValue(x => x.IsStreaming);

        SendCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canExecute: canSend);
        StopCommand = ReactiveCommand.Create(StopStreaming, canExecute: canStop);
        NewConversationCommand = ReactiveCommand.Create(StartNewConversation);
        ToggleListCommand = ReactiveCommand.Create(() => { IsListVisible = !IsListVisible; });
        TogglePanelCommand = ReactiveCommand.Create(() => { IsVisible = !IsVisible; });
        RegenerateCommand = ReactiveCommand.CreateFromTask<ChatMessageViewModel>(RegenerateAsync);
        SetScopeCommand = ReactiveCommand.Create<ConversationScope>(scope => SelectedScope = scope);

        Disposables.Add(SendCommand.ThrownExceptions
            .Subscribe(ex => ErrorMessage = ex.Message));
        Disposables.Add(RegenerateCommand.ThrownExceptions
            .Subscribe(ex => ErrorMessage = ex.Message));

        // Observe workspace changes to refresh the list
        Disposables.Add(
            workspace.WhenAnyValue(x => x.WorkspaceRoot)
                .Subscribe(root =>
                {
                    this.RaisePropertyChanged(nameof(HasWorkspace));
                    if (!string.IsNullOrEmpty(root))
                        _conversationList.SetWorkspaceRoot(root);
                }));

        // Open a conversation from the list
        Disposables.Add(
            _conversationList.ConversationSelected
                .Subscribe(id => _ = OpenConversationAsync(id)));
    }

    // ── Shell mode tracking ──────────────────────────────────────────────────

    private string _currentShellModeName = "Write";
    public string CurrentShellModeName
    {
        get => _currentShellModeName;
        set
        {
            _currentShellModeName = value;
            SelectedRole = _roleProvider.GetDefaultRole(value);
            this.RaisePropertyChanged(nameof(AvailableRoles));
            this.RaisePropertyChanged(nameof(CurrentShellModeName));
        }
    }

    // ── Message send flow ─────────────────────────────────────────────────────

    private async Task SendMessageAsync()
    {
        var text = ComposerText.Trim();
        if (string.IsNullOrEmpty(text)) return;

        ComposerText = string.Empty;
        ErrorMessage = null;
        IsProviderConfigured = _chatService.IsProviderConfigured;

        if (!IsProviderConfigured) return;

        // Ensure active conversation
        if (_activeConversation is null)
            await CreateNewConversationAsync(text).ConfigureAwait(false);

        // Build context bundle
        var contextBuilder = GetContextBuilderForCurrentMode();
        var systemPrompt = _roleProvider.GetSystemPrompt(SelectedRole, _workspace.Profile.Kind);
        var contextRequest = new ContextRequest(
            Scope: SelectedScope,
            WorkspaceRoot: _workspace.WorkspaceRoot,
            ManuscriptRoot: _workspace.ManuscriptRoot,
            BookTxtPath: _workspace.BookTxtPath,
            ActiveChapterRelativePath: _editor.ActiveNode?.RelativePath,
            LiveActiveChapterText: _editor.Text,
            RoleSystemPrompt: systemPrompt);

        string contextContent;
        try
        {
            contextContent = await contextBuilder.BuildContextAsync(contextRequest, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to build context: {ex.Message}";
            return;
        }

        // Build user message VM and persist to conversation
        var userMsgId = Guid.NewGuid().ToString();
        var userMsg = new ConversationMessage(userMsgId, "user", text, DateTimeOffset.UtcNow, null);
        _activeConversation = _activeConversation! with
        {
            Messages = new List<ConversationMessage>(_activeConversation.Messages) { userMsg },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        // Auto-title from first message
        if (_activeConversation.Messages.Count == 1)
        {
            var title = text.Length > 60 ? text[..60] + "…" : text;
            _activeConversation = _activeConversation with { Title = title };
            ConversationTitle = _activeConversation.Title;
        }

        Messages.Add(new ChatMessageViewModel(userMsgId, "user", text, userMsg.Timestamp));

        // Persist user message immediately
        await _conversationStore.SaveConversationAsync(_workspace.WorkspaceRoot, _activeConversation)
            .ConfigureAwait(false);
        await _conversationList.RefreshAsync().ConfigureAwait(false);

        // Build MEAI messages list
        var meaiMessages = BuildMeaiMessages(contextContent);

        // Stream response
        var assistantMsgVm = new ChatMessageViewModel(
            Guid.NewGuid().ToString(), "assistant", string.Empty, DateTimeOffset.UtcNow);
        assistantMsgVm.SetStreaming();
        Messages.Add(assistantMsgVm);

        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = new CancellationTokenSource();
        IsStreaming = true;

        try
        {
            await foreach (var chunk in _chatService.StreamAsync(meaiMessages, _streamCts.Token)
                               .ConfigureAwait(false))
            {
                assistantMsgVm.AppendChunk(chunk);
            }

            assistantMsgVm.FinalizeStreaming();

            // Persist completed assistant message
            var assistantMsg = new ConversationMessage(
                assistantMsgVm.Id, "assistant", assistantMsgVm.Content,
                assistantMsgVm.Timestamp, null);

            _activeConversation = _activeConversation with
            {
                Messages = new List<ConversationMessage>(_activeConversation.Messages) { assistantMsg },
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _conversationStore.SaveConversationAsync(_workspace.WorkspaceRoot, _activeConversation)
                .ConfigureAwait(false);
            await _conversationList.RefreshAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // User cancelled — remove streaming bubble, user message stays persisted
            Messages.Remove(assistantMsgVm);
        }
        catch (Exception ex)
        {
            assistantMsgVm.FinalizeStreaming();
            ErrorMessage = $"AI error: {ex.Message}";
        }
        finally
        {
            IsStreaming = false;
        }
    }

    private void StopStreaming()
    {
        _streamCts?.Cancel();
    }

    private async Task RegenerateAsync(ChatMessageViewModel lastAssistantMsg)
    {
        if (_activeConversation is null) return;

        // Remove the last assistant message from both lists
        var lastIdx = _activeConversation.Messages.Count - 1;
        if (lastIdx < 0 || _activeConversation.Messages[lastIdx].Role != "assistant") return;

        var msgs = _activeConversation.Messages.ToList();
        msgs.RemoveAt(lastIdx);
        _activeConversation = _activeConversation with { Messages = msgs };

        Messages.Remove(lastAssistantMsg);

        await _conversationStore.SaveConversationAsync(_workspace.WorkspaceRoot, _activeConversation)
            .ConfigureAwait(false);

        // Re-send (note: ComposerText is empty, so we'll push the last user message text)
        var lastUserMsg = _activeConversation.Messages.LastOrDefault(m => m.Role == "user");
        if (lastUserMsg is null) return;

        ComposerText = lastUserMsg.Content;
        await SendMessageAsync().ConfigureAwait(false);
    }

    // ── Conversation management ──────────────────────────────────────────────

    private void StartNewConversation()
    {
        _activeConversation = null;
        _activeConversationId = null;
        Messages.Clear();
        ConversationTitle = null;
        IsListVisible = false;
    }

    private async Task CreateNewConversationAsync(string firstMessageText)
    {
        var node = _editor.ActiveNode;
        var contextTag = new ContextTag(
            View: _currentShellModeName,
            Scope: _selectedScope.ToString(),
            ChapterPath: node?.RelativePath,
            ChapterTitle: node?.Title);

        var title = $"New conversation {DateTimeOffset.Now:yyyy-MM-dd}";
        var id = Guid.NewGuid().ToString();

        _activeConversation = new Conversation(
            Id: id,
            Title: title,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Archived: false,
            Role: SelectedRole.ToString(),
            ContextTag: contextTag,
            Messages: new List<ConversationMessage>());

        _activeConversationId = id;
        ConversationTitle = title;
    }

    private async Task OpenConversationAsync(string id)
    {
        if (string.IsNullOrEmpty(_workspace.WorkspaceRoot)) return;

        var conv = await _conversationStore.LoadConversationAsync(_workspace.WorkspaceRoot, id)
            .ConfigureAwait(false);

        if (conv is null)
        {
            _notifications.ShowError("Conversation data could not be loaded.");
            return;
        }

        _activeConversation = conv;
        _activeConversationId = id;
        ConversationTitle = conv.Title;
        IsListVisible = false;
        Messages.Clear();

        foreach (var msg in conv.Messages)
            Messages.Add(new ChatMessageViewModel(msg.Id, msg.Role, msg.Content, msg.Timestamp));
    }

    // ── Role change ─────────────────────────────────────────────────────────

    public async Task ChangeRoleAsync(AiRole newRole)
    {
        if (SelectedRole == newRole) return;
        SelectedRole = newRole;

        if (_activeConversation is null || string.IsNullOrEmpty(_workspace.WorkspaceRoot)) return;

        var systemMsg = new ConversationMessage(
            Guid.NewGuid().ToString(),
            "system",
            $"Role changed to {newRole}",
            DateTimeOffset.UtcNow,
            null);

        _activeConversation = _activeConversation with
        {
            Messages = new List<ConversationMessage>(_activeConversation.Messages) { systemMsg },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        Messages.Add(new ChatMessageViewModel(systemMsg.Id, "system", systemMsg.Content, systemMsg.Timestamp));

        await _conversationStore.SaveConversationAsync(_workspace.WorkspaceRoot, _activeConversation)
            .ConfigureAwait(false);
    }

    // ── MEAI message builder ─────────────────────────────────────────────────

    private List<ChatMessage> BuildMeaiMessages(string contextContent)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, contextContent)
        };

        if (_activeConversation is not null)
        {
            foreach (var m in _activeConversation.Messages)
            {
                if (m.Role == "user")
                    messages.Add(new ChatMessage(ChatRole.User, m.Content));
                else if (m.Role == "assistant")
                    messages.Add(new ChatMessage(ChatRole.Assistant, m.Content));
                // system (role-change) messages are skipped from MEAI history
            }
        }

        return messages;
    }

    private IContextBuilder GetContextBuilderForCurrentMode()
    {
        return _currentShellModeName switch
        {
            "Plan" => _planContextBuilder,
            "Manage" => _manageContextBuilder,
            _ => _writeContextBuilder,
        };
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        Disposables.Dispose();
    }
}
