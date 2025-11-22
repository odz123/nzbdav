import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { Tabs, Tab, Button, Form, Alert } from "react-bootstrap"
import { backendClient } from "~/clients/backend-client.server";
import { isUsenetSettingsUpdated, UsenetSettings } from "./usenet/usenet-multi";
import React, { useEffect } from "react";
import { isSabnzbdSettingsUpdated, isSabnzbdSettingsValid, SabnzbdSettings } from "./sabnzbd/sabnzbd";
import { isWebdavSettingsUpdated, isWebdavSettingsValid, WebdavSettings } from "./webdav/webdav";
import { isArrsSettingsUpdated, isArrsSettingsValid, ArrsSettings } from "./arrs/arrs";
import { Maintenance } from "./maintenance/maintenance";
import { isRepairsSettingsUpdated, isRepairsSettingsValid, RepairsSettings } from "./repairs/repairs";

const defaultConfig = {
    "general.base-url": "",
    "api.key": "",
    "api.categories": "",
    "api.manual-category": "uncategorized",
    "api.max-queue-connections": "",
    "api.ensure-importable-video": "true",
    "api.ensure-article-existence": "false",
    "api.ignore-history-limit": "true",
    "api.download-extension-blacklist": ".nfo, .par2, .sfv",
    "api.duplicate-nzb-behavior": "increment",
    "api.import-strategy": "symlinks",
    "api.completed-downloads-dir": "",
    "usenet.servers": "",
    "usenet.host": "",
    "usenet.port": "",
    "usenet.use-ssl": "false",
    "usenet.connections": "",
    "usenet.connections-per-stream": "5",
    "usenet.user": "",
    "usenet.pass": "",
    "webdav.user": "",
    "webdav.pass": "",
    "webdav.show-hidden-files": "false",
    "webdav.enforce-readonly": "true",
    "webdav.preview-par2-files": "false",
    "rclone.mount-dir": "",
    "media.library-dir": "",
    "arr.instances": "{\"RadarrInstances\":[],\"SonarrInstances\":[],\"QueueRules\":[]}",
    "repair.connections": "",
    "repair.enable": "false",
    "repair.sampling-rate": "0.15",
    "repair.min-segments": "10",
    "repair.adaptive-sampling": "true",
    "repair.cache-enabled": "true",
    "repair.cache-ttl-hours": "24",
    "repair.parallel-files": "3",
}

export async function loader({ request }: Route.LoaderArgs) {
    // fetch the config items
    var configItems = await backendClient.getConfig(Object.keys(defaultConfig));

    // transform to a map
    const config: Record<string, string> = defaultConfig;
    for (const item of configItems) {
        config[item.configName] = item.configValue;
    }
    return { config: config }
}

export default function Settings(props: Route.ComponentProps) {
    return (
        <Body config={props.loaderData.config} />
    );
}

type BodyProps = {
    config: Record<string, string>
};

function Body(props: BodyProps) {
    // stateful variables
    const [config, setConfig] = React.useState(props.config);
    const [newConfig, setNewConfig] = React.useState(config);
    const [isUsenetSettingsReadyToSave, setIsUsenetSettingsReadyToSave] = React.useState(false);
    const [isSaving, setIsSaving] = React.useState(false);
    const [isSaved, setIsSaved] = React.useState(false);
    const [saveError, setSaveError] = React.useState<string | null>(null);
    const [activeTab, setActiveTab] = React.useState('usenet');

    // derived variables
    const iseUsenetUpdated = isUsenetSettingsUpdated(config, newConfig);
    const isSabnzbdUpdated = isSabnzbdSettingsUpdated(config, newConfig);
    const isWebdavUpdated = isWebdavSettingsUpdated(config, newConfig);
    const isArrsUpdated = isArrsSettingsUpdated(config, newConfig);
    const isRepairsUpdated = isRepairsSettingsUpdated(config, newConfig);
    const isUpdated = iseUsenetUpdated || isSabnzbdUpdated || isWebdavUpdated || isArrsUpdated || isRepairsUpdated;

    const usenetTitle = iseUsenetUpdated ? "✏️ Usenet" : "Usenet";
    const sabnzbdTitle = isSabnzbdUpdated ? "✏️ SABnzbd " : "SABnzbd";
    const webdavTitle = isWebdavUpdated ? "✏️ WebDAV" : "WebDAV";
    const arrsTitle = isArrsUpdated ? "✏️ Radarr/Sonarr" : "Radarr/Sonarr";
    const repairsTitle = isRepairsUpdated ? "✏️ Repairs" : "Repairs";

    const saveButtonLabel = isSaving ? "Saving..."
        : !isUpdated && isSaved ? "Saved ✅"
        : !isUpdated && !isSaved ? "There are no changes to save"
        : isSabnzbdUpdated && !isSabnzbdSettingsValid(newConfig) ? "Invalid SABnzbd settings"
        : isWebdavUpdated && !isWebdavSettingsValid(newConfig) ? "Invalid WebDAV settings"
        : isArrsUpdated && !isArrsSettingsValid(newConfig) ? "Invalid Arrs settings"
        : isRepairsUpdated && !isRepairsSettingsValid(newConfig) ? "Invalid Repairs settings"
        : "Save";
    const saveButtonVariant = saveButtonLabel === "Save" ? "primary"
        : saveButtonLabel === "Saved ✅" ? "success"
        : "secondary";
    const isSaveButtonDisabled = saveButtonLabel !== "Save";

    // events
    const onClear = React.useCallback(() => {
        setNewConfig(config);
        setIsSaved(false);
    }, [config, setNewConfig]);

    const onUsenetSettingsReadyToSave = React.useCallback((isReadyToSave: boolean) => {
        setIsUsenetSettingsReadyToSave(isReadyToSave);
    }, [setIsUsenetSettingsReadyToSave]);

    const onSave = React.useCallback(async () => {
        setIsSaving(true);
        setIsSaved(false);
        setSaveError(null);
        try {
            const response = await fetch("/settings/update", {
                method: "POST",
                body: (() => {
                    const form = new FormData();
                    const changedConfig = getChangedConfig(config, newConfig);
                    form.append("config", JSON.stringify(changedConfig));
                    return form;
                })()
            });

            if (response.ok) {
                setConfig(newConfig);
                setIsSaved(true);
            } else {
                // Try to parse error message from response
                const errorData = await response.json().catch(() => ({ error: `Server returned status ${response.status}` }));
                setSaveError(errorData.error || errorData.message || `Failed to save settings (status ${response.status})`);
            }
        } catch (error) {
            setSaveError(error instanceof Error ? error.message : "Failed to save settings. Please check your connection and try again.");
        } finally {
            setIsSaving(false);
        }
    }, [config, newConfig, setIsSaving, setIsSaved, setSaveError, setConfig]);

    return (
        <div className={styles.container}>
            {saveError && (
                <Alert variant="danger" dismissible onClose={() => setSaveError(null)} className={styles.alert}>
                    <Alert.Heading>Error Saving Settings</Alert.Heading>
                    <p>{saveError}</p>
                </Alert>
            )}
            <Tabs
                activeKey={activeTab}
                onSelect={x => setActiveTab(x!)}
                className={styles.tabs}
            >
                <Tab eventKey="usenet" title={usenetTitle}>
                    <UsenetSettings config={newConfig} setNewConfig={setNewConfig} onReadyToSave={onUsenetSettingsReadyToSave} />
                </Tab>
                <Tab eventKey="sabnzbd" title={sabnzbdTitle}>
                    <SabnzbdSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="webdav" title={webdavTitle}>
                    <WebdavSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="arrs" title={arrsTitle}>
                    <ArrsSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="repairs" title={repairsTitle}>
                    <RepairsSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="maintenance" title="Maintenance">
                    <Maintenance savedConfig={config} />
                </Tab>
            </Tabs>
            <hr />
            {isUpdated && <Button
                className={styles.button}
                variant="secondary"
                disabled={!isUpdated}
                onClick={() => onClear()}>
                Clear
            </Button>}
            <Button
                className={styles.button}
                variant={saveButtonVariant}
                disabled={isSaveButtonDisabled}
                onClick={onSave}>
                {saveButtonLabel}
            </Button>
        </div>
    );
}

function getChangedConfig(
    config: Record<string, string>,
    newConfig: Record<string, string>
): Record<string, string> {
    let changedConfig: Record<string, string> = {};
    let configKeys = Object.keys(defaultConfig);
    for (const configKey of configKeys) {
        if (config[configKey] !== newConfig[configKey]) {
            // Special handling for password fields: only send if non-empty
            // Empty password means "keep current password"
            if (configKey === "webdav.pass" && !newConfig[configKey]) {
                continue; // Skip empty passwords
            }
            changedConfig[configKey] = newConfig[configKey];
        }
    }
    return changedConfig;
}