using AlmoSeis.SharedKernel.Application;

namespace AlmoSeis.Modules.Identity.Contracts;

// ---------- Aparelho compartilhado (06 §6.3, ADR-0027) ----------

public sealed record RegisterSharedDeviceCommand(string InstallationId, string Name, string Platform);

/// `deviceSecret` aparece só nesta resposta.
public sealed record RegisteredSharedDeviceDto(
    Guid Id, Guid TenantId, string InstallationId, string Name, string Platform, string DeviceSecret, DateTimeOffset RegisteredAt, AuthorDto RegisteredBy);

public sealed record SharedDeviceDto(Guid Id, string Name, string Platform, DateTimeOffset RegisteredAt, AuthorDto RegisteredBy, DateTimeOffset? RevokedAt);

public sealed record ListSharedDevicesQuery;

public sealed record RevokeSharedDeviceCommand(Guid DeviceId);

public sealed record DeviceRefDto(Guid Id, string Name);

public sealed record DeviceTenantDto(Guid Id, string Name);

public sealed record PinEmployeeDto(Guid Id, string DisplayName, IReadOnlyList<string> Roles);

public sealed record DeviceEmployeesDto(DeviceRefDto Device, DeviceTenantDto Tenant, IReadOnlyList<PinEmployeeDto> Employees);

/// Público, autenticado por `X-Tenant-Id` + `X-Device-Secret`.
public sealed record ListPinEmployeesQuery(Guid TenantId, string InstallationId, string? DeviceSecret);

public sealed record PinLoginRequest(Guid TenantId, string InstallationId, Guid EmployeeId, string Pin);

public sealed record PinLoginCommand(PinLoginRequest Request, string? DeviceSecret);

// ---------- Esqueci a senha (ADR-0040) ----------

public sealed record ForgotPasswordCommand(string Email);
