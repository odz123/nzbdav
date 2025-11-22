import { Form } from "react-bootstrap";
import styles from "./webdav.module.css"
import { type Dispatch, type SetStateAction, useState } from "react";
import { className } from "~/utils/styling";

type SabnzbdSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function WebdavSettings({ config, setNewConfig }: SabnzbdSettingsProps) {
    // Use separate state for password to avoid showing hash
    const [passwordValue, setPasswordValue] = useState("");
    const [isPasswordChanged, setIsPasswordChanged] = useState(false);

    // Get the display value for password (empty if not changed, or new value if changed)
    const displayPassword = isPasswordChanged ? passwordValue : "";

    // Handle password change
    const handlePasswordChange = (newPassword: string) => {
        setPasswordValue(newPassword);
        setIsPasswordChanged(true);
        setNewConfig({ ...config, "webdav.pass": newPassword });
    };

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Label htmlFor="webdav-user-input">WebDAV User</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidUser(config["webdav.user"]) && styles.error])}
                    type="text"
                    id="webdav-user-input"
                    aria-describedby="webdav-user-help"
                    placeholder="admin"
                    value={config["webdav.user"]}
                    onChange={e => setNewConfig({ ...config, "webdav.user": e.target.value })} />
                <Form.Text id="webdav-user-help" muted>
                    Use this user to connect to the webdav. Only letters, numbers, dashes, and underscores allowed.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="webdav-pass-input">WebDAV Password</Form.Label>
                <Form.Control
                    {...className([styles.input, isPasswordChanged && !isValidPassword(passwordValue) && styles.error])}
                    type="password"
                    id="webdav-pass-input"
                    aria-describedby="webdav-pass-help"
                    placeholder="Enter new password to change"
                    value={displayPassword}
                    onChange={e => handlePasswordChange(e.target.value)} />
                <Form.Text id="webdav-pass-help" muted>
                    Use this password to connect to the webdav. Minimum 8 characters required. Leave blank to keep current password.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="readonly-checkbox"
                    aria-describedby="readonly-help"
                    label={`Enforce Read-Only`}
                    checked={config["webdav.enforce-readonly"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.enforce-readonly": "" + e.target.checked })} />
                <Form.Text id="readonly-help" muted>
                    The WebDAV `/content` folder will be readonly when checked. WebDAV clients will not be able to delete files within this directory.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="show-hidden-files-checkbox"
                    aria-describedby="show-hidden-files-help"
                    label={`Show hidden files on Dav Explorer`}
                    checked={config["webdav.show-hidden-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.show-hidden-files": "" + e.target.checked })} />
                <Form.Text id="show-hidden-files-help" muted>
                    Hidden files or directories are those whose names are prefixed by a period.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="preview-par2-files-checkbox"
                    aria-describedby="preview-par2-files-help"
                    label={`Preview par2 files on Dav Explorer`}
                    checked={config["webdav.preview-par2-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.preview-par2-files": "" + e.target.checked })} />
                <Form.Text id="preview-par2-files-help" muted>
                    When enabled, par2 files will be rendered as text files on the Dav Explorer page, displaying all File-Descriptor entries.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isWebdavSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["webdav.user"] !== newConfig["webdav.user"]
        || config["webdav.pass"] !== newConfig["webdav.pass"]
        || config["webdav.show-hidden-files"] !== newConfig["webdav.show-hidden-files"]
        || config["webdav.enforce-readonly"] !== newConfig["webdav.enforce-readonly"]
        || config["webdav.preview-par2-files"] !== newConfig["webdav.preview-par2-files"]
}

export function isWebdavSettingsValid(newConfig: Record<string, string>, passwordChanged: boolean = true) {
    // User must always be valid
    if (!isValidUser(newConfig["webdav.user"])) return false;

    // Password only needs to be valid if it's being changed
    // If not changed (empty string), we keep the existing password
    if (passwordChanged && newConfig["webdav.pass"]) {
        return isValidPassword(newConfig["webdav.pass"]);
    }

    return true;
}

function isValidUser(user: string): boolean {
    const regex = /^[A-Za-z0-9_-]+$/;
    return regex.test(user);
}

function isValidPassword(password: string): boolean {
    const MIN_PASSWORD_LENGTH = 8;
    return password && password.length >= MIN_PASSWORD_LENGTH;
}