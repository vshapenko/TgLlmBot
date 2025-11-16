using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.DataAccess.EntityTypeConfigurations;

public class KickedUserEntityTypeConfiguration : IEntityTypeConfiguration<KickedUser>
{
    public void Configure(EntityTypeBuilder<KickedUser> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasKey(x => new { x.ChatId, x.Id });
    }
}
