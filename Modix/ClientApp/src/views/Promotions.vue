<template>
    <div class="promotions">
        <section class="section">
            <div class="container">

                <div class="level">

                    <div class="level-left">

                        <div class="level-item">
                            <h1 class="title">
                                Promotion Campaigns
                            </h1>
                        </div>

                    </div>

                    <div class="level-right columns is-mobile">

                        <div class="column">
                            <label class="checkbox">
                                <input type="checkbox" v-model="showInactive">
                                Show Inactive
                            </label>
                        </div>

                        <div class="column is-narrow">
                            <router-link class="button is-pulled-right" to="/promotions/create">Start One</router-link>
                        </div>

                    </div>

                </div>

                <a v-if="loading" class="button is-primary is-loading campaignLoader"></a>

                <p v-else-if="campaigns.length == 0">
                    There's no active campaigns at the moment. You could start one, though!
                </p>
                <PromotionListItem v-for="campaign in campaigns" :campaign="campaign" :key="campaign.id"
                                   :dialogLoading="currentlyLoadingInfractions == campaign.id" @commentSubmitted="refresh()" @showPanel="showPanel(campaign)"
                                   v-on:comment-edit-modal-opened="onCommentEditModalOpened" />
            </div>
        </section>

        <div class="modal" :class="{'is-active': showModal}">
            <div class="modal-background" @click="toggleModal()"></div>
            <div class="modal-card">
                <template v-if="modalCampaign">
                    <header class="modal-card-head">
                        <p class="modal-card-title">
                            <strong>{{modalCampaign.subject.displayName}}</strong>'s Campaign
                        </p>

                        <div class="field has-addons is-hidden-mobile">
                            <div class="control is-expanded">
                                <a class="copyButton is-small button" title="Copy to Clipboard"
                                   :data-clipboard-text="'!info ' + modalCampaign.subject.id">
                                    &#128203;
                                </a>
                            </div>
                            <div class="control">
                                <input class="input is-small" readonly :value="'!info ' + modalCampaign.subject.id" />
                            </div>
                        </div>

                        <button class="delete" aria-label="close" @click="toggleModal()"></button>
                    </header>
                    <section class="modal-card-body">
                        <h4 class="title is-size-4">
                            Infractions
                            <router-link :to="`/logs/infractions?subject=${modalCampaign.subject.id}`">
                                (Full View)
                            </router-link>
                        </h4>

                        <InfractionTable v-if="canSeeInfractions"
                            :recordsPage="modalCampaignInfractions" :showActions="false" :minimal="true"
                            :showState="false" :showDeleted="false" :staticFilters="{}">
                        </InfractionTable>

                        <h6 v-else>You can't see infractions because you're missing the ModerationRead claim.</h6>
                    </section>
                    <footer class="modal-card-foot level" v-if="canCloseCampaign">
                        <div class="level-left">
                            <button class="button is-success" :class="{'is-loading': modifyLoading}" :disabled="modalCampaign.closeAction" @click="promote()">Accept</button>
                            <label class="checkbox">
                                <input type="checkbox" v-model="modalForceAccept">
                                Force?
                            </label>
                        </div>
                        <div class="level-right">
                            <button class="button is-danger" :class="{'is-loading': modifyLoading}" :disabled="modalCampaign.closeAction" @click="deny()">Reject</button>
                        </div>
                    </footer>
                </template>
            </div>
        </div>

        <PromotionCommentEditModal v-bind:comment="commentToEdit" v-bind:showUpdateModal="showCommentEditModal"
                                   v-on:comment-edit-modal-closed="onCommentEditModalClosing" v-on:comment-edited="refresh" />

    </div>
</template>

<style scoped lang="scss">

@import "~bulma/sass/utilities/_all";

.modal
{
    z-index: -999;

    &.is-active
    {
        z-index: 999;
    }
}

code
{
    color: gray;
}

.modal-card-head
{
    input
    {
        width: 14em;
    }

    .field.has-addons
    {
        position: relative;
        top: 6px;
        left: -20px;
    }
}

.modal-card
{
    .modal-card-body ol
    {
        margin-left: 1em;
    }

    @include tablet-only()
    {
        width: 95vw !important;
    }

    @include desktop()
    {
        width: 75vw !important;
    }
}

.level-left + .level-right
{
    margin-top: 0;
}

.level
{
    justify-content: space-between;
}

.campaignLoader
{
    width: 100%;
    height: 64px;
}

</style>

<script lang="ts">
import { Component, Prop, Vue, Watch } from 'vue-property-decorator';
import HeroHeader from '@/components/HeroHeader.vue';
import PromotionListItem from '@/components/Promotions/PromotionListItem.vue';
import PromotionCommentView from '@/components/Promotions/PromotionCommentView.vue';

import store from "@/app/Store";
import * as _ from 'lodash';
import PromotionCampaign from '@/models/promotions/PromotionCampaign';
import PromotionService from '@/services/PromotionService';
import {config, setConfig} from '@/models/PersistentConfig';
import InfractionSummary from '@/models/infractions/InfractionSummary';
import GeneralService from '@/services/GeneralService';
import PromotionComment from '@/models/promotions/PromotionComment';
import PromotionCommentEditModal from '@/components/Promotions/PromotionCommentEditModal.vue';
import InfractionTable from '@/components/Logs/InfractionTable.vue';

import {formatDate} from '@/app/Util';
import ModixComponent from '@/components/ModixComponent.vue';
import RecordsPage from '@/models/RecordsPage';
import SortParameter from '@/models/SortParameter';
import { SortDirection } from '@/models/SortDirection';
import TableParameters from '@/models/TableParameters';

var Clipboard = require('clipboard');

@Component({
    components:
    {
        HeroHeader,
        PromotionListItem,
        PromotionCommentView,
        PromotionCommentEditModal,
        InfractionTable
    },
})
export default class Promotions extends ModixComponent
{
    showInactive: boolean = false;
    showModal: boolean = false;

    modalCampaign: PromotionCampaign | null = null;
    modalCampaignInfractions: RecordsPage<InfractionSummary> = new RecordsPage<InfractionSummary>();
    modalForceAccept: boolean = false;

    currentlyLoadingInfractions: number | null = null;
    loading: boolean = false;
    modifyLoading: boolean = false;

    commentToEdit: PromotionComment | null = null;
    showCommentEditModal: boolean = false;

    get campaigns(): PromotionCampaign[]
    {
        let campaigns = this.$store.state.modix.campaigns as PromotionCampaign[];

        let ordered = _.orderBy(campaigns,
        [
            campaign => campaign.isActive,
            campaign => campaign.startDate
        ], ['desc', 'desc']);

        return _.filter(ordered, campaign => (this.showInactive ? true : campaign.isActive));
    }

    get canSeeInfractions()
    {
        return store.userHasClaims(["ModerationRead"]);
    }

    get canCloseCampaign()
    {
        return store.userHasClaims(["PromotionsCloseCampaign"]);
    }

    @Watch('showInactive')
    inactiveChanged()
    {
        setConfig(conf => conf.showInactiveCampaigns = this.showInactive);
    }

    async created()
    {
        this.showInactive = config().showInactiveCampaigns;
        await this.refresh();
    }

    async refresh()
    {
        this.showCommentEditModal = false;

        this.loading = true;

        await store.retrieveRoles();
        await store.retrieveCampaigns();

        this.loading = false;
    }

    async showPanel(campaign: PromotionCampaign)
    {
        this.currentlyLoadingInfractions = campaign.id;

        this.modalCampaign = campaign;

        await this.refreshModalCampaignInfractions();

        this.modalForceAccept = false;

        this.toggleModal();

        this.currentlyLoadingInfractions = null;
    }

    async refreshModalCampaignInfractions()
    {
        if (this.state.user && store.userHasClaims(["ModerationRead"]))
        {
            this.modalCampaignInfractions = await GeneralService.getInfractions({
                page: 0,
                perPage: 9999,
                sort: { field: "created", direction: SortDirection.Descending },
                filters: [
                    { field: "subject", value: this.modalCampaign!.subject!.id.toString() }
                ]
            });
        }
    }

    toggleModal()
    {
        this.showModal = !this.showModal;
    }

    async promote()
    {
        if (this.modalCampaign == null) { return; }

        this.modifyLoading = true;

        try
        {
            if (this.modalForceAccept)
            {
                await PromotionService.forceApproveCampaign(this.modalCampaign);
            }
            else
            {
                await PromotionService.approveCampaign(this.modalCampaign);
            }
        }
        catch (err)
        {
            store.pushErrorMessage(err.response.data);
        }
        finally
        {
            this.modifyLoading = false;

            this.toggleModal();
            await this.refresh();
        }
    }

    async deny()
    {
        if (this.modalCampaign == null) { return; }

        this.modifyLoading = true;

        try
        {
            await PromotionService.denyCampaign(this.modalCampaign);
        }
        catch (err)
        {
            store.pushErrorMessage(err.response.data);
        }
        finally
        {
            this.modifyLoading = false;

            this.toggleModal();
            await this.refresh();
        }
    }

    onCommentEditModalOpened(comment: PromotionComment)
    {
        this.commentToEdit = comment;
        this.showCommentEditModal = true;
    }

    onCommentEditModalClosing() : void
    {
        this.showCommentEditModal = false;
    }

    mounted()
    {
        new Clipboard('.copyButton');
    }

    formatDate(date: Date)
    {
        return formatDate(date);
    }

    updated()
    {

    }
}
</script>
