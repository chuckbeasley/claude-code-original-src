// TaskItem, TaskStatus, and TaskStoreState have been moved to
// ClaudeCode.Core.Tasks to eliminate a circular project dependency.
// Global type aliases in GlobalAliases.cs re-bind those names
// project-wide so that all existing "using ClaudeCode.Tools.TaskStore;"
// consumers continue to compile without modification.

namespace ClaudeCode.Tools.TaskStore;
