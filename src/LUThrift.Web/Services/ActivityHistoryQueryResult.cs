using LUThrift.Web.ViewModels;

namespace LUThrift.Web.Services;

public enum ActivityHistoryQueryStatus { Success, AuthenticationRequired, AdministratorRequired, InvalidInput }

public sealed record ActivityHistoryQueryResult(
    ActivityHistoryQueryStatus Status, string Message, ActivityHistoryPage? Page = null);
