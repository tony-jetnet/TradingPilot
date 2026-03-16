using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Volo.Abp.AspNetCore.Mvc.UI.RazorPages;
using Volo.Abp.Localization;

namespace TradingPilot.Pages;

public class IndexModel : AbpPageModel
{
    public IReadOnlyList<LanguageInfo>? Languages { get; protected set; }

    public string? CurrentLanguage { get; protected set; }

    protected ILanguageProvider LanguageProvider { get; }

    public IndexModel(ILanguageProvider languageProvider)
    {
        LanguageProvider = languageProvider;
    }

    public async Task OnGetAsync()
    {
        Languages = await LanguageProvider.GetLanguagesAsync();
        CurrentLanguage = CultureInfo.CurrentCulture.DisplayName;
    }
}
