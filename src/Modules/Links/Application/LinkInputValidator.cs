using FluentValidation;
using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;
using Zaagl.SharedKernel.ShortCodes;

namespace Zaagl.Modules.Links.Application;

public sealed class LinkInputValidator : AbstractValidator<LinkInput>
{
    public LinkInputValidator(CodeGenerator codes)
    {
        RuleFor(x => x.DestinationUrl).NotEmpty().MaximumLength(UrlSafety.MaxLength);

        RuleFor(x => x.CustomSlug)
            .Must(SlugRules.IsValidFormat)
            .WithMessage($"Slug must be {SlugRules.MinCustomLength}-{SlugRules.MaxLength} characters: letters, digits, '-' or '_', starting with a letter or digit.")
            .Must(s => !SlugRules.IsReserved(s!))
            .WithMessage("This slug is reserved.")
            // A custom alias the generator could one day produce would collide with a future generated
            // code (spec §4.5): reject it now, the same way a reserved word is rejected.
            .Must(s => !codes.IsGeneratorReachable(s!))
            .WithMessage("This slug is reserved.")
            .When(x => !string.IsNullOrEmpty(x.CustomSlug));

        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000);

        RuleFor(x => x.Tags)
            .Must(t => t!.Length <= 10 && t.All(s => !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= 30))
            .WithMessage("Use at most 10 tags, each 1-30 characters.")
            .When(x => x.Tags is not null);

        RuleFor(x => x.Rules)
            .Must(r => r!.Length <= 20).WithMessage("At most 20 routing rules are allowed.")
            .When(x => x.Rules is not null);
        RuleForEach(x => x.Rules).SetValidator(new RuleInputValidator());

        When(x => x.OpenGraph is not null, () =>
        {
            RuleFor(x => x.OpenGraph!.Title).MaximumLength(200);
            RuleFor(x => x.OpenGraph!.Description).MaximumLength(500);
            RuleFor(x => x.OpenGraph!.ImageUrl).MaximumLength(UrlSafety.MaxLength);
        });

        When(x => x.Policy is not null, () =>
        {
            RuleFor(x => x.Policy!.MaxClicks).GreaterThan(0).LessThanOrEqualTo(100_000_000)
                .When(x => x.Policy!.MaxClicks is not null);
            RuleFor(x => x.Policy!.Password).MinimumLength(4).MaximumLength(128)
                .When(x => !string.IsNullOrEmpty(x.Policy!.Password));
            RuleFor(x => x.Policy!.FallbackUrl).MaximumLength(UrlSafety.MaxLength);
        });
    }
}

public sealed class RuleInputValidator : AbstractValidator<RuleInput>
{
    public RuleInputValidator()
    {
        RuleFor(x => x.Kind)
            .Must(k => RuleKinds.All.Contains(k?.ToLowerInvariant()))
            .WithMessage("Rule kind must be device, country or split.");

        RuleFor(x => x.Url).NotEmpty().MaximumLength(UrlSafety.MaxLength);
        RuleFor(x => x.Weight).InclusiveBetween(1, 1000);
        RuleFor(x => x.Priority).InclusiveBetween(0, 1000);

        RuleFor(x => x.Match)
            .Must(m => RuleKinds.Devices.Contains(m?.ToLowerInvariant()))
            .WithMessage("Device rules must match ios, android or desktop.")
            .When(x => string.Equals(x.Kind, RuleKinds.Device, StringComparison.OrdinalIgnoreCase));

        RuleFor(x => x.Match)
            .Must(m => m is { Length: 2 } && m.All(char.IsAsciiLetter))
            .WithMessage("Country rules must use a two-letter country code.")
            .When(x => string.Equals(x.Kind, RuleKinds.Country, StringComparison.OrdinalIgnoreCase));
    }
}
