import type { Route } from "./+types/route";
import { backendClient, type ConfigItem } from "~/clients/backend-client.server";
import { redirect } from "react-router";
import { isAuthenticated } from "~/auth/authentication.server";

export async function action({ request }: Route.ActionArgs) {
    // ensure user is logged in
    if (!await isAuthenticated(request)) return redirect("/login");

    try {
        // get the ConfigItems to update
        const formData = await request.formData();
        const configJson = formData.get("config");

        if (!configJson) {
            return { error: "No configuration data provided", success: false };
        }

        const config = JSON.parse(configJson.toString());
        const configItems: ConfigItem[] = [];
        for (const [key, value] of Object.entries<string>(config)) {
            configItems.push({
                configName: key,
                configValue: value
            })
        }

        // update the config items
        await backendClient.updateConfig(configItems);
        return { config: config, success: true }
    } catch (error) {
        const errorMessage = error instanceof Error ? error.message : "Unknown error occurred";
        return { error: errorMessage, success: false };
    }
}