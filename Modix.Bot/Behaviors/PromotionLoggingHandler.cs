﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Discord;
using Discord.WebSocket;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Modix.Data.Messages;
using Modix.Data.Models.Core;
using Modix.Data.Models.Promotions;
using Modix.Data.Repositories;
using Modix.Data.Utilities;
using Modix.Services.Core;
using Modix.Services.Promotions;

namespace Modix.Behaviors
{
    /// <summary>
    /// Renders moderation actions, as they are created, as messages to each configured moderation log channel.
    /// </summary>
    public class PromotionLoggingHandler :
        INotificationHandler<PromotionActionCreated>
    {
        /// <summary>
        /// Constructs a new <see cref="PromotionLoggingHandler"/> object, with injected dependencies.
        /// </summary>
        public PromotionLoggingHandler(
            IServiceProvider serviceProvider,
            IDiscordClient discordClient,
            IDesignatedChannelService designatedChannelService,
            IUserService userService)
        {
            DiscordClient = discordClient;
            DesignatedChannelService = designatedChannelService;
            UserService = userService;

            _lazyPromotionsService = new Lazy<IPromotionsService>(() => serviceProvider.GetRequiredService<IPromotionsService>());
        }
        
        public Task Handle(PromotionActionCreated notification, CancellationToken cancellationToken)
            => OnPromotionActionCreatedAsync(notification.PromotionActionId, notification.PromotionActionCreationData);

        public async Task OnPromotionActionCreatedAsync(long promotionActionId, PromotionActionCreationData data)
        {
            if (await DesignatedChannelService.AnyDesignatedChannelAsync(data.GuildId, DesignatedChannelType.PromotionLog))
            {
                var message = await FormatPromotionLogEntry(promotionActionId, data);

                if (message == null)
                    return;

                await DesignatedChannelService.SendToDesignatedChannelsAsync(
                    await DiscordClient.GetGuildAsync(data.GuildId), DesignatedChannelType.PromotionLog, message);
            }

            if (await DesignatedChannelService.AnyDesignatedChannelAsync(data.GuildId, DesignatedChannelType.PromotionNotifications))
            {
                var embed = await FormatPromotionNotification(promotionActionId, data);

                if (embed == null)
                    return;

                await DesignatedChannelService.SendToDesignatedChannelsAsync(
                    await DiscordClient.GetGuildAsync(data.GuildId), DesignatedChannelType.PromotionNotifications, "", embed);
            }
        }

        private async Task<Embed> FormatPromotionNotification(long promotionActionId, PromotionActionCreationData data)
        {
            var promotionAction = await PromotionsService.GetPromotionActionSummaryAsync(promotionActionId);
            var targetCampaign = promotionAction.Campaign ?? promotionAction.NewComment.Campaign;

            var embed = new EmbedBuilder();

            if (promotionAction.Type != PromotionActionType.CampaignClosed) { return null; }
            if (targetCampaign.Outcome != PromotionCampaignOutcome.Accepted) { return null; }

            var boldName = $"**{targetCampaign.Subject.Username}#{targetCampaign.Subject.Discriminator}**";
            var boldRole = $"**{MentionUtils.MentionRole(targetCampaign.TargetRole.Id)}**";

            var subject = await UserService.GetUserInformationAsync(data.GuildId, targetCampaign.Subject.Id);

            embed = embed
                .WithTitle("The campaign is over!")
                .WithDescription($"Staff accepted the campaign, and {boldName} was promoted to {boldRole}! 🎉")
                .WithAuthor(subject)
                .WithFooter("See more at https://mod.gg/promotions");

            return embed.Build();
        }

        private async Task<string> FormatPromotionLogEntry(long promotionActionId, PromotionActionCreationData data)
        {
            var promotionAction = await PromotionsService.GetPromotionActionSummaryAsync(promotionActionId);
            var key = (promotionAction.Type, promotionAction.NewComment?.Sentiment, promotionAction.Campaign?.Outcome);

            if (!_logRenderTemplates.TryGetValue(key, out var renderTemplate))
                return null;

            return string.Format(renderTemplate,
                   promotionAction.Created.UtcDateTime.ToString("HH:mm:ss"),
                   promotionAction.Campaign?.Id,
                   promotionAction.Campaign?.Subject.DisplayName,
                   promotionAction.Campaign?.Subject.Id,
                   promotionAction.Campaign?.TargetRole.Name,
                   promotionAction.Campaign?.TargetRole.Id,
                   promotionAction.NewComment?.Campaign.Id,
                   promotionAction.NewComment?.Campaign.Subject.DisplayName,
                   promotionAction.NewComment?.Campaign.Subject.Id,
                   promotionAction.NewComment?.Campaign.TargetRole.Name,
                   promotionAction.NewComment?.Campaign.TargetRole.Id,
                   promotionAction.NewComment?.Content);
        }

        /// <summary>
        /// An <see cref="IDiscordClient"/> for interacting with the Discord API.
        /// </summary>
        internal protected IDiscordClient DiscordClient { get; }

        /// <summary>
        /// An <see cref="IDesignatedChannelService"/> for logging moderation actions.
        /// </summary>
        internal protected IDesignatedChannelService DesignatedChannelService { get; }

        /// <summary>
        /// An <see cref="IUserService"/> for retrieving user info
        /// </summary>
        internal protected IUserService UserService { get; }

        /// <summary>
        /// An <see cref="IPromotionsService"/> for performing moderation actions.
        /// </summary>
        internal protected IPromotionsService PromotionsService
            => _lazyPromotionsService.Value;
        private readonly Lazy<IPromotionsService> _lazyPromotionsService;

        private static ConcurrentDictionary<long, EmbedBuilder> _initialCommentQueue = new ConcurrentDictionary<long, EmbedBuilder>();

        private static readonly Dictionary<(PromotionActionType, PromotionSentiment?, PromotionCampaignOutcome?), string> _logRenderTemplates
            = new Dictionary<(PromotionActionType, PromotionSentiment?, PromotionCampaignOutcome?), string>()
            {
                { (PromotionActionType.CampaignCreated,  null,                       null),                              "`[{0}]` A campaign (`{1}`) was created to promote **{2}** (`{3}`) to **{4}** (`{5}`)." },
                { (PromotionActionType.CommentCreated,   PromotionSentiment.Abstain, null),                              "`[{0}]` A comment was added to the campaign (`{6}`) to promote **{7}** (`{8}`) to **{9}** (`{10}`), abstaining from the campaign. ```{11}```" },
                { (PromotionActionType.CommentCreated,   PromotionSentiment.Approve, null),                              "`[{0}]` A comment was added to the campaign (`{6}`) to promote **{7}** (`{8}`) to **{9}** (`{10}`), approving of the promotion. ```{11}```" },
                { (PromotionActionType.CommentCreated,   PromotionSentiment.Oppose,  null),                              "`[{0}]` A comment was added to the campaign (`{6}`) to promote **{7}** (`{8}`) to **{9}** (`{10}`), opposing the promotion. ```{11}```" },
                { (PromotionActionType.CommentModified,  PromotionSentiment.Abstain, null),                              "`[{0}]` A comment was modified in the campaign (`{6}`) to promote **{7}** (`{8}`) to **{9}** (`{10}`), abstaining from the campaign. ```{11}```" },
                { (PromotionActionType.CommentModified,  PromotionSentiment.Approve, null),                              "`[{0}]` A comment was modified in the campaign (`{6}`) to promote **{7}** (`{8}`) to **{9}** (`{10}`), approving of the promotion. ```{11}```" },
                { (PromotionActionType.CommentModified,  PromotionSentiment.Oppose,  null),                              "`[{0}]` A comment was modified in the campaign (`{6}`) to promote **{7}** (`{8}`) to **{9}** (`{10}`), opposing the promotion. ```{11}```" },
                { (PromotionActionType.CampaignClosed,   null,                       PromotionCampaignOutcome.Accepted), "`[{0}]` The campaign (`{1}`) to promote **{2}** (`{3}`) to **{4}** (`{5}`) was accepted." },
                { (PromotionActionType.CampaignClosed,   null,                       PromotionCampaignOutcome.Rejected), "`[{0}]` The campaign (`{1}`) to promote **{2}** (`{3}`) to **{4}** (`{5}`) was rejected." },
                { (PromotionActionType.CampaignClosed,   null,                       PromotionCampaignOutcome.Failed),   "`[{0}]` The campaign (`{1}`) to promote **{2}** (`{3}`) to **{4}** (`{5}`) failed to process." },
            };
    }
}
