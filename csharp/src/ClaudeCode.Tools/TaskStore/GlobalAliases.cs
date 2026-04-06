// Global type aliases so that all ClaudeCode.Tools files that do
// "using ClaudeCode.Tools.TaskStore;" continue to resolve TaskItem,
// TaskStoreState, and TaskStatus after those types were moved to
// ClaudeCode.Core.Tasks to break a circular project dependency
// (ClaudeCode.Services.Tasks execution types also need TaskStoreState,
// and ClaudeCode.Services does not reference ClaudeCode.Tools).

global using TaskItem = ClaudeCode.Core.Tasks.TaskItem;
global using TaskStoreState = ClaudeCode.Core.Tasks.TaskStoreState;
global using TaskStatus = ClaudeCode.Core.Tasks.TaskStatus;
