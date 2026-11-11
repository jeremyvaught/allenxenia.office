using AllenBuilding.Application.Knowledge;
using AllenBuilding.Infrastructure.Persistence;

namespace AllenBuilding.Web.Hosting;

public static class KnowledgeRegistration
{
    public static void AddOfficeKnowledge(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IKnowledgeStore, KnowledgeStore>();
        builder.Services.AddScoped<KnowledgeActions>();
    }
}
