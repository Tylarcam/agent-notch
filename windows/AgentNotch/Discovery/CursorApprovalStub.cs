namespace AgentNotch.Discovery;

// Cursor permission prompts: no stable Windows hook yet (unlike Claude's
// notify.ps1 Notification path). ApprovalStore only reads ~/.config/agent-notch/pending
// today; a future Cursor hook should write the same JSON shape there.
// Demo/preview approval cards use seeded PendingApproval on AgentSession.
