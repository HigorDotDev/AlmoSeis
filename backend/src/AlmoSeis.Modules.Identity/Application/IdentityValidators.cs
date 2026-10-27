using AlmoSeis.Modules.Identity.Contracts;
using FluentValidation;

namespace AlmoSeis.Modules.Identity.Application;

internal static class IdentityRules
{
    public const string PhonePattern = @"^\+[1-9][0-9]{7,14}$";

    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage("Informe a senha.")
            .MinimumLength(8).WithMessage("A senha precisa de pelo menos 8 caracteres.")
            .MaximumLength(128);

    public static IRuleBuilderOptions<T, string> InstallationId<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(64);
}

internal sealed class SignUpCommandValidator : AbstractValidator<SignUpCommand>
{
    public SignUpCommandValidator()
    {
        RuleFor(x => x)
            .Must(x => (x.NewAccount is null) != (x.ExistingAccount is null))
            .OverridePropertyName("newAccount")
            .WithMessage("Informe uma conta nova ou uma conta existente (exatamente uma).");

        When(x => x.NewAccount is not null, () =>
        {
            RuleFor(x => x.NewAccount!.FullName).NotEmpty().MaximumLength(120);
            RuleFor(x => x.NewAccount!.Email).NotEmpty().EmailAddress().MaximumLength(254)
                .WithMessage("O proprietário precisa de um e-mail válido (recuperação de senha).");
            RuleFor(x => x.NewAccount!.PhoneNumber).Matches(IdentityRules.PhonePattern)
                .When(x => x.NewAccount!.PhoneNumber is not null)
                .WithMessage("Telefone no formato +5534999990001.");
            RuleFor(x => x.NewAccount!.Password).StrongPassword();
        });

        When(x => x.ExistingAccount is not null, () =>
        {
            RuleFor(x => x.ExistingAccount!.Login).NotEmpty();
            RuleFor(x => x.ExistingAccount!.Password).NotEmpty();
        });

        RuleFor(x => x.Tenant).NotNull();
        RuleFor(x => x.Tenant.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Tenant.DocumentType).Must(t => t is "CPF" or "CNPJ")
            .WithMessage("Use CPF ou CNPJ.");
        RuleFor(x => x.Tenant.DocumentNumber)
            .Must((cmd, doc) => doc is not null && doc.All(char.IsAsciiDigit)
                && doc.Length == (cmd.Tenant.DocumentType == "CPF" ? 11 : 14))
            .WithMessage("Documento só com dígitos (11 para CPF, 14 para CNPJ).");
        RuleFor(x => x.Tenant.City).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Tenant.State).Matches("^[A-Za-z]{2}$").WithMessage("UF com 2 letras.");
        RuleFor(x => x.Tenant.TimeZone)
            .Must(tz => tz is null || TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
            .WithMessage("Fuso horário IANA inválido.");
        RuleFor(x => x.AcceptedTermsVersion).NotEmpty().MaximumLength(20);
        RuleFor(x => x.InstallationId).InstallationId();
    }
}

internal sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Login).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        RuleFor(x => x.InstallationId).InstallationId();
    }
}

internal sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(200);
        RuleFor(x => x.InstallationId).InstallationId();
    }
}

internal sealed class SwitchTenantCommandValidator : AbstractValidator<SwitchTenantCommand>
{
    public SwitchTenantCommandValidator() => RuleFor(x => x.TenantId).NotEmpty();
}

internal sealed class UpdateTenantRequestValidator : AbstractValidator<UpdateTenantRequest>
{
    public UpdateTenantRequestValidator()
    {
        RuleFor(x => x.Name.Value).NotEmpty().MaximumLength(120).When(x => x.Name.HasValue).OverridePropertyName("name");
        RuleFor(x => x.City.Value).NotEmpty().MaximumLength(80).When(x => x.City.HasValue).OverridePropertyName("city");
        RuleFor(x => x.State.Value).Matches("^[A-Za-z]{2}$").WithMessage("Use a sigla do estado, ex.: MG.").When(x => x.State.HasValue).OverridePropertyName("state");
        RuleFor(x => x.TimeZone.Value).NotEmpty().MaximumLength(64).When(x => x.TimeZone.HasValue).OverridePropertyName("timeZone");
    }
}

internal sealed class UpdateEmployeeRequestValidator : AbstractValidator<UpdateEmployeeRequest>
{
    public UpdateEmployeeRequestValidator()
    {
        RuleFor(x => x.DisplayName.Value).NotEmpty().MaximumLength(60).When(x => x.DisplayName.HasValue).OverridePropertyName("displayName");
        RuleFor(x => x.Roles.Value).NotEmpty().WithMessage("Escolha pelo menos um perfil.").When(x => x.Roles.HasValue).OverridePropertyName("roles");
    }
}

internal sealed class CreateInvitationCommandValidator : AbstractValidator<CreateInvitationCommand>
{
    public CreateInvitationCommandValidator()
    {
        RuleFor(x => x.Roles).NotEmpty().WithMessage("Escolha pelo menos um perfil.");
    }
}

internal sealed class AcceptInvitationRequestValidator : AbstractValidator<AcceptInvitationRequest>
{
    public AcceptInvitationRequestValidator()
    {
        RuleFor(x => x).Must(x => (x.NewAccount is null) != (x.ExistingAccount is null))
            .WithMessage("Informe uma conta nova ou uma conta existente.").OverridePropertyName("newAccount");
        RuleFor(x => x.NewAccount!.FullName).NotEmpty().MaximumLength(120).When(x => x.NewAccount is not null).OverridePropertyName("newAccount.fullName");
        RuleFor(x => x.NewAccount!.Password).StrongPassword().When(x => x.NewAccount is not null).OverridePropertyName("newAccount.password");
        RuleFor(x => x.NewAccount!.PhoneNumber).Matches(IdentityRules.PhonePattern).When(x => x.NewAccount?.PhoneNumber is not null)
            .OverridePropertyName("newAccount.phoneNumber");
        RuleFor(x => x.NewAccount!).Must(a => a.Email is not null || a.PhoneNumber is not null)
            .WithMessage("Informe e-mail ou telefone.").When(x => x.NewAccount is not null).OverridePropertyName("newAccount");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(60);
        RuleFor(x => x.InstallationId).InstallationId();
    }
}

internal sealed class SetOwnPinCommandValidator : AbstractValidator<SetOwnPinCommand>
{
    public SetOwnPinCommandValidator()
    {
        RuleFor(x => x.Pin).NotEmpty();
        RuleFor(x => x.CurrentPassword).NotEmpty();
    }
}

internal sealed class RegisterSharedDeviceCommandValidator : AbstractValidator<RegisterSharedDeviceCommand>
{
    public RegisterSharedDeviceCommandValidator()
    {
        RuleFor(x => x.InstallationId).InstallationId();
        RuleFor(x => x.Name).NotEmpty().WithMessage("Dê um nome ao aparelho.").MaximumLength(40);
        RuleFor(x => x.Platform).Must(p => p is "ANDROID" or "IOS").WithMessage("Use ANDROID ou IOS.");
    }
}

internal sealed class PinLoginRequestValidator : AbstractValidator<PinLoginRequest>
{
    public PinLoginRequestValidator()
    {
        RuleFor(x => x.InstallationId).InstallationId();
        RuleFor(x => x.Pin).NotEmpty().MaximumLength(6);
    }
}

internal sealed class ForgotPasswordCommandValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(254);
    }
}

internal sealed class AdminLoginCommandValidator : AbstractValidator<AdminLoginCommand>
{
    public AdminLoginCommandValidator()
    {
        RuleFor(x => x.Login).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

internal sealed class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator()
    {
        RuleFor(x => x.NewPassword).StrongPassword();
    }
}
