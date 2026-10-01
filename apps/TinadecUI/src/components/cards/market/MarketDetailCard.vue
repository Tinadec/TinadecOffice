<script setup lang="ts">
import {
  Bot,
  Boxes,
  CheckCircle2,
  Download,
  Globe2,
  Hourglass,
  PlugZap,
  ShieldCheck,
  Terminal,
  Trash2,
  X,
} from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { UiButton } from '@/components/ui'
import { marketController } from '@/controllers/MarketController'

const { t } = useI18n()

const {
  busy, activeProposal: proposal, proposalBusy, targetProject,
  selectedItem, selectedInstallation, awaitingDecision,
  mcpServers, mcpReadSucceeded, mcpReason, mcpConfigPath,
  previewInstall, previewRemoval, applyProposal, discardProposal,
} = marketController

function kindIcon(kind: string) {
  if (kind === 'skill') return Bot
  if (kind === 'mcp-server') return PlugZap
  if (kind === 'acp-adapter') return Terminal
  return Boxes
}

/** A proposal is reviewed, then queued, then a human decides elsewhere; this line says which. */
const removal = computed(() => proposal.value?.action === 'uninstall')

/** The command exactly as it will be written, so the reviewer reads one line not two lists. */
const commandLine = computed(() => {
  const pending = proposal.value
  if (!pending?.command) return ''
  return [pending.command, ...pending.args].join(' ')
})

/** Core's `expires_at` is a deadline, not a duration; show the clock time the reviewer can check. */
const expiresAtLabel = computed(() => {
  const raw = proposal.value?.expires_at
  if (!raw) return ''
  const moment = new Date(raw)
  return Number.isNaN(moment.getTime()) ? raw : moment.toLocaleTimeString()
})

function statusLine() {
  const row = selectedInstallation.value
  if (!row) return t('market.notInstalled')
  if (awaitingDecision(row)) return t('market.awaitingDecisionHint')
  if (row.state === 'removing') return t('market.removingHint')
  if (row.action_status === 'completed') return t('market.installedHint')
  return `${t('market.installingHint')} · ${row.action_status}`
}
</script>

<template vapor>
  <aside class="market-detail">
    <template v-if="selectedItem">
      <div class="market-detail-head">
        <div class="market-detail-icon" :class="selectedItem.kind">
          <component :is="kindIcon(selectedItem.kind)" :size="22" />
        </div>
        <div>
          <h2>{{ selectedItem.display_name }}</h2>
          <p>{{ selectedItem.extension_id }}</p>
        </div>
      </div>

      <p class="market-detail-copy">{{ selectedItem.description }}</p>

      <div class="market-status-strip" :class="{ enabled: !!selectedInstallation }">
        <CheckCircle2 v-if="selectedInstallation" :size="16" />
        <ShieldCheck v-else :size="16" />
        <span>{{ statusLine() }}</span>
      </div>

      <div class="market-detail-grid">
        <div>
          <span>{{ t('market.source') }}</span>
          <strong>{{ selectedItem.source_name }}</strong>
        </div>
        <div>
          <span>{{ t('market.version') }}</span>
          <strong>{{ selectedItem.version }}</strong>
        </div>
      </div>

      <div v-if="selectedItem.transports.length" class="market-section">
        <h3>{{ t('market.transports') }}</h3>
        <div class="market-chip-row wrap">
          <span v-for="transport in selectedItem.transports" :key="transport">{{ transport }}</span>
        </div>
      </div>

      <div v-if="selectedItem.homepage" class="market-section">
        <h3>{{ t('market.homepage') }}</h3>
        <p class="market-detail-copy">{{ selectedItem.homepage }}</p>
      </div>

      <!-- A listing can be true and still not be something this tool layer can start. Say which
           case it is instead of offering a button that would only fail. -->
      <p v-if="selectedItem.installable === false" class="market-blocker" data-testid="market-blocker">
        {{ selectedItem.install_blocker || t('market.notInstallable') }}
      </p>

      <div v-if="proposal" class="market-proposal" data-testid="market-proposal">
        <div class="market-proposal-head">
          <h3>{{ removal ? t('market.removalTitle') : t('market.installTitle') }}</h3>
          <UiButton variant="ghost" size="icon" :title="t('market.discard')" @click="discardProposal">
            <X :size="14" />
          </UiButton>
        </div>

        <dl class="market-proposal-grid">
          <div>
            <dt>{{ t('market.pinnedVersion') }}</dt>
            <dd>{{ proposal.version }}</dd>
          </div>
          <div>
            <dt>{{ t('market.targetProject') }}</dt>
            <dd>{{ targetProject?.name ?? proposal.project_id }}</dd>
          </div>
          <div v-if="commandLine">
            <dt>{{ t('market.command') }}</dt>
            <dd class="mono">{{ commandLine }}</dd>
          </div>
          <div v-if="proposal.replaces_command">
            <dt>{{ t('market.replaces') }}</dt>
            <dd class="mono strike">{{ proposal.replaces_command }}</dd>
          </div>
          <div>
            <dt>{{ t('market.configFile') }}</dt>
            <dd class="mono">{{ proposal.target_path }}</dd>
          </div>
          <div>
            <dt>{{ t('market.expiresAt') }}</dt>
            <dd>{{ expiresAtLabel }}</dd>
          </div>
        </dl>

        <div v-if="proposal.environment.length" class="market-section">
          <h3>{{ t('market.environment') }}</h3>
          <!-- Names and flags only: Core's proposal cannot carry a value, and one entered here
               would never reach the file. -->
          <div class="market-chip-row wrap">
            <span v-for="entry in proposal.environment" :key="entry.name">
              {{ entry.name }}<template v-if="entry.required"> · {{ t('market.envRequired') }}</template><template v-if="entry.secret"> · {{ t('market.envSecret') }}</template>
            </span>
          </div>
          <p class="quiet">{{ t('market.environmentNote') }}</p>
        </div>

        <div v-if="proposal.warnings.length" class="market-section">
          <h3>{{ t('market.warnings') }}</h3>
          <ul class="market-warning-list">
            <li v-for="warning in proposal.warnings" :key="warning">{{ warning }}</li>
          </ul>
        </div>

        <details class="market-proposal-bytes">
          <summary>{{ t('market.showBytes') }}</summary>
          <!-- The reviewed bytes, not a summary of them: what is approved here is what lands. -->
          <pre>{{ proposal.content }}</pre>
        </details>

        <div class="market-action-row">
          <UiButton :disabled="busy || proposalBusy" @click="applyProposal">
            <Hourglass :size="15" />
            {{ t('market.queueForApproval') }}
          </UiButton>
          <UiButton variant="ghost" size="sm" :disabled="busy" @click="discardProposal">
            {{ t('common.cancel') }}
          </UiButton>
        </div>
      </div>

      <div class="market-section" data-testid="market-mcp">
        <h3>{{ t('market.mcpServers') }}</h3>
        <!-- Three different answers, three different sentences. An empty list and an unreadable
             list used to look identical, which is how "the Tool Provider never started" read as
             "you have no MCP servers". -->
        <p v-if="!mcpReadSucceeded" class="quiet" data-testid="market-mcp-unavailable">
          {{ mcpReason || t('market.mcpUnreadable') }}
        </p>
        <template v-else>
          <p v-if="mcpServers.length === 0" class="quiet" data-testid="market-mcp-none">
            {{ t('market.mcpNone') }}
          </p>
          <div
            class="market-runtime-line"
            v-for="server in mcpServers"
            :key="server.id"
            :data-status="server.status"
            data-testid="market-mcp-server"
          >
            <Globe2 :size="14" />
            <span>{{ server.name }} · {{ server.status }}</span>
          </div>
          <p v-if="mcpConfigPath" class="quiet">{{ t('market.mcpConfigFile') }}: {{ mcpConfigPath }}</p>
        </template>
      </div>

      <div class="market-action-row">
        <UiButton
          v-if="!proposal"
          :disabled="busy || proposalBusy || selectedItem.installable === false"
          :title="selectedItem.installable === false ? (selectedItem.install_blocker ?? t('market.notInstallable')) : undefined"
          @click="previewInstall"
        >
          <Download :size="15" />
          {{ t('market.previewInstall') }}
        </UiButton>
        <UiButton v-if="!proposal && selectedInstallation && selectedInstallation.state !== 'removing'" variant="secondary" :disabled="busy || proposalBusy" @click="previewRemoval">
          <Trash2 :size="15" />
          {{ t('market.previewRemoval') }}
        </UiButton>
      </div>
      <p class="quiet">{{ t('market.approvalWhere') }}</p>
    </template>

    <p v-else class="market-detail-copy quiet">{{ t('market.empty') }}</p>
  </aside>
</template>

<style scoped>
.market-detail {
  display: flex;
  flex-direction: column;
  gap: 16px;
  height: 100%;
  overflow-y: auto;
  padding: 16px;
}

.market-detail-head {
  display: flex;
  align-items: center;
  gap: 12px;
}

.market-detail-head h2 {
  font-size: 16px;
  font-weight: 600;
  margin: 0;
}

.market-detail-head p {
  font-size: 12px;
  color: var(--text-secondary);
  margin: 0;
}

.market-status-strip {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-radius: 6px;
  background: var(--surface-hover);
  font-size: 13px;
}

.market-status-strip.enabled {
  color: var(--accent-success, #10b981);
}

.market-detail-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
  font-size: 13px;
}

.market-chip-row.wrap {
  flex-wrap: wrap;
}

.market-blocker {
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 12px;
  margin: 0;
  padding: 8px 12px;
}

.market-proposal {
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 12px;
}

.market-proposal-head {
  align-items: center;
  display: flex;
  justify-content: space-between;
}

.market-proposal-head h3 {
  font-size: 13px;
  font-weight: 600;
  margin: 0;
}

.market-proposal-grid {
  display: grid;
  gap: 8px 12px;
  font-size: 12px;
  grid-template-columns: 1fr;
  margin: 0;
}

.market-proposal-grid dt {
  color: var(--text-secondary);
}

.market-proposal-grid dd {
  margin: 0;
  /* The line a human is approving must be readable whole; an ellipsis would hide the argument
     that decides it. */
  overflow-wrap: anywhere;
  white-space: normal;
}

.market-proposal-grid .mono {
  font-family: monospace;
}

.market-proposal-grid .strike {
  color: var(--text-secondary);
  text-decoration: line-through;
}

.market-warning-list {
  font-size: 12px;
  margin: 0;
  padding-left: 18px;
}

.market-proposal-bytes summary {
  color: var(--text-secondary);
  cursor: pointer;
  font-size: 12px;
}

.market-proposal-bytes pre {
  background: var(--surface-input);
  border-radius: 6px;
  font-size: 11px;
  margin: 8px 0 0;
  max-height: 220px;
  overflow: auto;
  padding: 8px;
  white-space: pre-wrap;
}

.market-action-row {
  display: flex;
  gap: 8px;
}
</style>
