# Pre-Launch QA Report - NzbDav
**Generated:** 2025-11-22
**Status:** 🚨 CRITICAL ISSUES FOUND - DO NOT LAUNCH YET

## Executive Summary

This comprehensive audit has identified **28 issues** that could significantly impact user experience in production. These include **7 CRITICAL** issues, **12 HIGH** priority issues, and **9 MEDIUM** priority issues.

**Recommendation:** Address all CRITICAL and HIGH priority issues before launch.

---

## 🔴 CRITICAL ISSUES (Must Fix Before Launch)

### CRIT-1: Session Key Regeneration on Restart Will Log Out All Users
**Location:** `frontend/app/auth/authentication.server.ts:19`
**Severity:** CRITICAL
**Impact:** Every time the container restarts, all users will be logged out

**Problem:**
```typescript
secrets: [process?.env?.SESSION_KEY || crypto.randomBytes(64).toString('hex')],
```

If `SESSION_KEY` environment variable is not set, a random key is generated on each server start. This means:
- Container restarts = all users logged out
- Horizontal scaling = sessions won't work across instances
- Poor user experience and confusion

**Fix:** Require `SESSION_KEY` environment variable or persist it to disk/database.

---

### CRIT-2: Frontend Dependency Vulnerabilities
**Location:** `frontend/package.json`
**Severity:** CRITICAL
**Impact:** Security vulnerabilities in production

**Problems:**
1. **glob (10.2.0 - 10.4.5)** - HIGH severity: Command injection via -c/--cmd
2. **vite (6.0.0 - 6.4.0)** - MODERATE severity: Multiple file serving vulnerabilities

**Fix:** Run `npm audit fix` in the frontend directory immediately.

---

### CRIT-3: No Error Feedback When Settings Save Fails
**Location:** `frontend/app/routes/settings/route.tsx:120-137`
**Severity:** CRITICAL
**Impact:** Users have no way to know if their settings failed to save

**Problem:**
```typescript
const onSave = React.useCallback(async () => {
    setIsSaving(true);
    setIsSaved(false);
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
    }
    setIsSaving(false);
    setIsSaved(true);
}, [config, newConfig, setIsSaving, setIsSaved, setConfig]);
```

If `response.ok` is false, the UI still shows "Saved ✅" which is misleading. Users won't know their settings failed to save.

**Fix:** Check response status and show error message to user when save fails.

---

### CRIT-4: Settings Update Route Has No Error Handling
**Location:** `frontend/app/routes/settings.update/route.tsx:6-25`
**Severity:** CRITICAL
**Impact:** Backend errors during settings update are not caught or displayed to users

**Problem:**
```typescript
export async function action({ request }: Route.ActionArgs) {
    if (!await isAuthenticated(request)) return redirect("/login");

    const formData = await request.formData();
    const configJson = formData.get("config")!.toString();
    const config = JSON.parse(configJson);
    const configItems: ConfigItem[] = [];
    for (const [key, value] of Object.entries<string>(config)) {
        configItems.push({
            configName: key,
            configValue: value
        })
    }

    await backendClient.updateConfig(configItems);
    return { config: config }
}
```

No try-catch block. If `updateConfig` throws an error, the user gets no feedback.

**Fix:** Wrap in try-catch and return error message that the frontend can display.

---

### CRIT-5: WebDAV Password May Be Stored in Plain Text
**Location:** `backend/Config/ConfigManager.cs:150-157`
**Severity:** CRITICAL
**Impact:** Security vulnerability if database is compromised

**Problem:**
```csharp
public string? GetWebdavPasswordHash()
{
    var hashedPass = StringUtil.EmptyToNull(GetConfigValue("webdav.pass"));
    if (hashedPass != null) return hashedPass;
    var pass = Environment.GetEnvironmentVariable("WEBDAV_PASSWORD");
    if (pass != null) return PasswordUtil.Hash(pass);
    return null;
}
```

The method name is `GetWebdavPasswordHash()` but it returns `GetConfigValue("webdav.pass")` directly, assuming it's already hashed. However, there's no guarantee it's hashed when stored via the UI.

**Fix:** Verify that passwords are always hashed before being stored in the database. Check the update config endpoint.

---

### CRIT-6: No Validation for Empty WebDAV Password
**Location:** `frontend/app/routes/settings/webdav/webdav.tsx:32-38`
**Severity:** CRITICAL
**Impact:** Users can save empty WebDAV password, breaking authentication

**Problem:**
```typescript
<Form.Control
    className={styles.input}
    type="password"
    id="webdav-pass-input"
    aria-describedby="webdav-pass-help"
    value={config["webdav.pass"]}
    onChange={e => setNewConfig({ ...config, "webdav.pass": e.target.value })} />
```

No validation to prevent empty password. `isWebdavSettingsValid()` only checks username format.

**Fix:** Add password validation (minimum length, not empty).

---

### CRIT-7: Missing Non-Null Assertion Operator Could Cause Runtime Error
**Location:** `frontend/app/routes/settings.update/route.tsx:12`
**Severity:** HIGH
**Impact:** Application crash if formData doesn't contain "config"

**Problem:**
```typescript
const configJson = formData.get("config")!.toString();
```

Using `!` assumes "config" always exists. If it doesn't, this will throw a runtime error.

**Fix:** Add null check with proper error handling.

---

## 🟠 HIGH PRIORITY ISSUES

### HIGH-1: Poor UX - Using window.alert() for User Feedback
**Location:** `frontend/app/routes/settings/usenet/usenet-multi.tsx:122-128`
**Severity:** HIGH
**Impact:** Unprofessional user experience, alerts can be blocked by browsers

**Problem:**
```typescript
const handleTestServer = useCallback(async (server: UsenetServerConfig) => {
    try {
        const response = await fetch("/api/test-usenet-connection", {
            // ... fetch logic
        });

        const data = await response.json();
        const success = response.ok && data?.connected === true;

        if (success) {
            alert(`Connection to ${server.name} successful!`);
        } else {
            alert(`Connection to ${server.name} failed. Please check your credentials.`);
        }
    } catch (error) {
        alert(`Failed to test connection to ${server.name}.`);
    }
}, []);
```

**Fix:** Use React Bootstrap `Toast` or `Alert` components for notifications.

---

### HIGH-2: Poor UX - Using window.confirm() for Deletion
**Location:** `frontend/app/routes/settings/usenet/usenet-multi.tsx:92-95`
**Severity:** HIGH
**Impact:** Inconsistent UI, poor mobile experience

**Problem:**
```typescript
const handleDeleteServer = useCallback((serverId: string) => {
    if (confirm("Are you sure you want to delete this server?")) {
        setServers(prevServers => prevServers.filter(s => s.id !== serverId));
    }
}, []);
```

**Fix:** Use a proper modal dialog component for confirmation.

---

### HIGH-3: No Loading State for Connection Tests
**Location:** `frontend/app/routes/settings/usenet/usenet-multi.tsx:103-129`
**Severity:** HIGH
**Impact:** Users don't know if test is running, may click multiple times

**Problem:** When user clicks "Test Connection", there's no visual feedback that the test is running.

**Fix:** Add loading spinner/disabled state to test button during the request.

---

### HIGH-4: Typo in User-Facing Text
**Location:** `frontend/app/routes/settings/maintenance/maintenance.tsx:27`
**Severity:** HIGH
**Impact:** Unprofessional appearance

**Problem:**
```typescript
Convert Strm Files to Symlnks  // Should be "Symlinks"
```

**Fix:** Correct the spelling.

---

### HIGH-5: No User Feedback During Onboarding Password Mismatch
**Location:** `frontend/app/routes/onboarding/route.tsx:45-48`
**Severity:** HIGH
**Impact:** Submit button is disabled with message, but no visual indication of which field has the issue

**Problem:**
```typescript
} else if (password != confirmPassword) {
    submitButtonDisabled = true;
    submitButtonText = "Passwords must match";
}
```

While the button text changes, there's no red border or error message next to the password fields.

**Fix:** Add visual error indicators (red border) to the password fields when they don't match.

---

### HIGH-6: Console.error in Production Code
**Location:** `frontend/app/routes/settings/usenet/usenet-multi.tsx:28`
**Severity:** HIGH
**Impact:** Error information leaked to browser console, debugging info visible to users

**Problem:**
```typescript
} catch (e) {
    console.error("Failed to parse usenet.servers:", e);
}
```

**Fix:** Remove console.error or use proper error logging service.

---

### HIGH-7: No CORS Configuration
**Location:** `backend/Program.cs`
**Severity:** HIGH
**Impact:** May cause issues if frontend and backend are on different domains/ports

**Problem:** No CORS middleware configured. This could be intentional if they're always on the same origin, but it should be documented.

**Fix:** Either add CORS configuration or document why it's not needed.

---

### HIGH-8: No Rate Limiting on API Endpoints
**Location:** `backend/Program.cs`
**Severity:** HIGH
**Impact:** API abuse, DOS attacks possible

**Problem:** No rate limiting middleware found.

**Fix:** Add rate limiting for public API endpoints, especially authentication endpoints.

---

### HIGH-9: Password Validation Only on Client Side
**Location:** `frontend/app/routes/onboarding/route.tsx:39-48`
**Severity:** HIGH
**Impact:** Client-side validation can be bypassed

**Problem:** Password requirements (not empty, must match) are only validated on the frontend.

**Fix:** Add server-side validation in the create account endpoint.

---

### HIGH-10: No Minimum Password Length Requirement
**Location:** `frontend/app/routes/onboarding/route.tsx`
**Severity:** HIGH
**Impact:** Users can create weak passwords like "1" or "a"

**Problem:** No minimum length validation for passwords.

**Fix:** Enforce minimum password length (e.g., 8 characters) with clear messaging.

---

### HIGH-11: API Key Visible in Settings UI
**Location:** `frontend/app/routes/settings/sabnzbd/sabnzbd.tsx:23-28`
**Severity:** HIGH
**Impact:** API key displayed in plain text, could be shoulder-surfed

**Problem:**
```typescript
<Form.Control
    type="text"  // Should be "password" type
    id="api-key-input"
    aria-describedby="api-key-help"
    value={config["api.key"]}
    readOnly />
```

**Fix:** Use `type="password"` with a toggle to reveal. Or add a "Copy to Clipboard" button instead.

---

### HIGH-12: WebDAV Password Visible in Settings UI
**Location:** `frontend/app/routes/settings/webdav/webdav.tsx:32-38`
**Severity:** HIGH
**Impact:** Password visible when editing settings

**Problem:** Password input field will show the hashed password as plain text, which is confusing.

**Fix:** Don't populate password field with existing value. Show placeholder "••••••••" and only update if user enters new value.

---

## 🟡 MEDIUM PRIORITY ISSUES

### MED-1: Mobile Breakpoint May Not Be Optimal
**Location:** `frontend/app/routes/_index/components/page-layout/page-layout.module.css:53`
**Severity:** MEDIUM
**Impact:** May not look good on tablets

**Problem:**
```css
@media not (min-width: 900px)  {
```

900px breakpoint seems arbitrary. Industry standard is usually 768px (iPad) or 1024px.

**Fix:** Test on actual devices and adjust breakpoint as needed.

---

### MED-2: Tables May Not Be Responsive
**Location:** `frontend/app/routes/queue/components/queue-table/queue-table.tsx`
**Severity:** MEDIUM
**Impact:** Tables may overflow on mobile devices

**Problem:** Need to verify tables have horizontal scroll or responsive layout on mobile.

**Fix:** Add overflow-x: auto or use responsive table patterns.

---

### MED-3: Long Filenames May Break Layout
**Location:** `frontend/app/routes/queue/components/queue-table/queue-table.tsx`
**Severity:** MEDIUM
**Impact:** UI may break with very long NZB names

**Problem:** No visible text truncation strategy for long filenames.

**Fix:** Verify word-break or truncation is applied to filename columns.

---

### MED-4: No Favicon or App Icon
**Location:** Project root
**Severity:** MEDIUM
**Impact:** Unprofessional appearance, poor browser tab UX

**Problem:** No evidence of favicon.ico or app icons.

**Fix:** Add favicon and touch icons for mobile devices.

---

### MED-5: No Keyboard Shortcuts
**Location:** Throughout application
**Severity:** MEDIUM
**Impact:** Power users can't navigate efficiently

**Problem:** No keyboard shortcuts for common actions (save settings, navigate tabs, etc.)

**Fix:** Add keyboard shortcuts for frequently used actions.

---

### MED-6: No Loading State for Initial Page Load
**Location:** `frontend/app/routes/queue/route.tsx:30-42`
**Severity:** MEDIUM
**Impact:** User sees blank screen while data loads

**Problem:** Loader data is fetched but there's no loading skeleton/spinner shown during initial load.

**Fix:** Add loading skeleton components.

---

### MED-7: WebSocket Reconnection Shows No User Feedback
**Location:** `frontend/app/routes/queue/route.tsx:119-146`
**Severity:** MEDIUM
**Impact:** Users don't know if live updates are working

**Problem:** WebSocket connection status is not shown to users. If connection fails, they don't know live updates are disabled.

**Fix:** Add connection status indicator in UI.

---

### MED-8: No Accessibility (ARIA) Labels on Some Buttons
**Location:** Various components
**Severity:** MEDIUM
**Impact:** Screen reader users may have difficulty

**Problem:** Some interactive elements may lack proper ARIA labels.

**Fix:** Audit all interactive elements for accessibility.

---

### MED-9: No Dark/Light Mode Toggle
**Location:** Application-wide
**Severity:** LOW-MEDIUM
**Impact:** Users can't choose preferred theme

**Problem:** Application is dark mode only.

**Fix:** Consider adding theme toggle (optional enhancement).

---

## Additional Observations

### Positive Findings ✅
1. ✅ Good error handling in ExceptionMiddleware
2. ✅ Proper SQL injection prevention via Entity Framework
3. ✅ Authentication properly implemented with session cookies
4. ✅ CSRF protection via SameSite cookies
5. ✅ Good use of loading states for form submissions
6. ✅ Client-side validation is comprehensive
7. ✅ WebSocket has exponential backoff for reconnection
8. ✅ No .env files checked into git
9. ✅ Docker multi-stage builds for optimization
10. ✅ Proper password hashing with PasswordUtil

### Code Quality Notes
- No TODO/FIXME comments found in user-facing code (good)
- Documentation files suggest ongoing development and bug tracking (good)
- Performance optimizations already implemented (good)

---

## Recommended Action Plan

### Phase 1: Pre-Launch (DO NOT SKIP)
1. ✅ Fix CRIT-1: Add SESSION_KEY environment variable requirement
2. ✅ Fix CRIT-2: Run npm audit fix
3. ✅ Fix CRIT-3: Add error feedback for settings save failures
4. ✅ Fix CRIT-4: Add try-catch to settings update route
5. ✅ Fix CRIT-5: Verify password hashing on save
6. ✅ Fix CRIT-6: Add WebDAV password validation
7. ✅ Fix CRIT-7: Add null check for formData.get("config")

### Phase 2: Launch Week
1. Fix all HIGH priority issues (HIGH-1 through HIGH-12)
2. Test on mobile devices
3. Run security audit
4. Load testing

### Phase 3: Post-Launch
1. Address MEDIUM priority issues based on user feedback
2. Implement additional UX enhancements
3. Add analytics/monitoring

---

## Testing Checklist

Before launch, ensure:
- [ ] All CRITICAL issues are fixed
- [ ] Settings can be saved and loaded correctly
- [ ] Error messages are shown to users when operations fail
- [ ] WebDAV authentication works with password
- [ ] Onboarding flow works end-to-end
- [ ] Login/logout works and sessions persist across browser restarts
- [ ] Mobile layout is usable on phones (< 900px width)
- [ ] Connection tests provide proper feedback
- [ ] npm audit shows no vulnerabilities
- [ ] Container restarts don't log out users (SESSION_KEY test)

---

**End of Report**
