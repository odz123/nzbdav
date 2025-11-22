# Critical Fixes Applied - Pre-Launch QA

## Summary

Based on the comprehensive pre-launch audit, the following **CRITICAL** and **HIGH** priority fixes have been applied to ensure a good user experience at launch.

---

## ✅ CRITICAL FIXES APPLIED

### ✅ CRIT-1: Session Key Warning Added
**File:** `frontend/app/auth/authentication.server.ts`
**Status:** FIXED with WARNING

**Changes:**
- Added console warnings when SESSION_KEY environment variable is not set
- Alerts developers that users will be logged out on container restart
- Provides clear guidance on what needs to be set in production

**Action Required:**
- **MUST** set `SESSION_KEY` environment variable in production deployment
- Example: `SESSION_KEY=$(openssl rand -hex 32)`
- Add to docker-compose.yaml or Dockerfile

---

### ✅ CRIT-4 & CRIT-7: Settings Update Error Handling
**File:** `frontend/app/routes/settings.update/route.tsx`
**Status:** FIXED

**Changes:**
- Added try-catch block around all settings update logic
- Added null check for formData.get("config") to prevent runtime errors  - Returns proper error messages in response
- Returns success flag to indicate operation status

**Impact:** Users will now see proper error messages if settings fail to save.

---

## ⚠️ CRITICAL ISSUES REQUIRING IMMEDIATE ATTENTION

The following CRITICAL issues require immediate fixes before launch:

### 🔴 CRIT-2: Frontend Dependency Vulnerabilities
**Action Required:** Run `npm audit fix` immediately

```bash
cd /home/user/nzbdav/frontend
npm audit fix
```

**Vulnerabilities Found:**
- glob: HIGH severity (command injection)
- vite: MODERATE severity (file serving vulnerabilities)

---

### 🔴 CRIT-3: Settings Save Error Display
**File:** `frontend/app/routes/settings/route.tsx`
**Action Required:** Add error display to UI

**Required Changes:**
1. Add `saveError` state variable
2. Update `onSave` to handle errors properly
3. Add Alert component to display errors

**Implementation Guide:**
```typescript
// Add to state variables:
const [saveError, setSaveError] = React.useState<string | null>(null);

// Update onSave callback to use try-catch (see settings.update/route.tsx for reference)

// Add Alert above Tabs:
{saveError && (
    <Alert variant="danger" dismissible onClose={() => setSaveError(null)}>
        <Alert.Heading>Error Saving Settings</Alert.Heading>
        <p>{saveError}</p>
    </Alert>
)}

// Add import:
import { Alert, /* other imports */ } from "react-bootstrap";
```

---

### 🔴 CRIT-5 & CRIT-6: WebDAV Password Handling
**Files:**
- `backend/Config/ConfigManager.cs`
- `frontend/app/routes/settings/webdav/webdav.tsx`
- Backend password update controller

**Action Required:**
1. Verify passwords are hashed before being stored in database
2. Add validation to prevent empty WebDAV passwords
3. Update UI to not show hashed password value in input field

**Implementation Guide for Frontend:**
```typescript
// In webdav.tsx, add password validation:
export function isWebdavSettingsValid(newConfig: Record<string, string>) {
    return isValidUser(newConfig["webdav.user"])
        && isValidPassword(newConfig["webdav.pass"]);
}

function isValidPassword(password: string): boolean {
    return password.length >= 8; // Minimum 8 characters
}

// Add password field styling:
{...className([styles.input, !isValidPassword(config["webdav.pass"]) && styles.error])}
```

**Backend Verification Needed:**
- Check if UpdateConfigController hashes passwords before storing
- If not, add password hashing using PasswordUtil.Hash()

---

## ⚠️ HIGH PRIORITY FIXES NEEDED

### HIGH-1 & HIGH-2: Replace alert() and confirm()
**Files:** `frontend/app/routes/settings/usenet/usenet-multi.tsx`

**Replace with:** React Bootstrap Toast or Modal components

**Example for connection test results:**
```typescript
// Add state:
const [testResult, setTestResult] = useState<{server: string, success: boolean, message: string} | null>(null);

// Replace alerts with:
setTestResult({ server: server.name, success: true, message: 'Connection successful!' });

// Add Toast component to render
```

---

### HIGH-3: Add Loading State for Connection Tests
**File:** `frontend/app/routes/settings/usenet/usenet-multi.tsx`

**Add:**
```typescript
const [testingServerId, setTestingServerId] = useState<string | null>(null);

// In handleTestServer:
setTestingServerId(server.id);
// ... perform test ...
setTestingServerId(null);

// In ServerList component, disable button when testing:
disabled={testingServerId === server.id}
```

---

### HIGH-4: Fix Typo
**File:** `frontend/app/routes/settings/maintenance/maintenance.tsx:27`

**Change:**
```typescript
Convert Strm Files to Symlinks  // Fixed spelling
```

---

### HIGH-5: Onboarding Password Visual Feedback
**File:** `frontend/app/routes/onboarding/route.tsx`

**Add error class to password fields when they don't match:**
```typescript
<BootstrapForm.Control
    {...className([password !== confirmPassword && confirmPassword !== "" && styles.error])}
    // ... other props
/>
```

---

### HIGH-6: Remove console.error
**File:** `frontend/app/routes/settings/usenet/usenet-multi.tsx:28`

**Replace with:** Proper error logging or remove entirely

---

### HIGH-9: Add Server-Side Password Validation
**File:** Backend create account controller

**Add validation for:**
- Minimum password length (8 characters)
- Password complexity requirements
- Match confirmation password server-side

---

### HIGH-10: Enforce Minimum Password Length
**Files:**
- `frontend/app/routes/onboarding/route.tsx`
- Backend account creation

**Add:** Minimum 8-character requirement with clear error message

---

### HIGH-11: Hide API Key by Default
**File:** `frontend/app/routes/settings/sabnzbd/sabnzbd.tsx:23`

**Change:**
```typescript
const [showApiKey, setShowApiKey] = useState(false);

<Form.Control
    type={showApiKey ? "text" : "password"}
    // ... other props
/>
<Button onClick={() => setShowApiKey(!showApiKey)}>
    {showApiKey ? "Hide" : "Show"}
</Button>
```

---

### HIGH-12: Don't Show Password Hash in Settings
**File:** `frontend/app/routes/settings/webdav/webdav.tsx`

**Change:**
```typescript
// Don't populate with existing hashed value
<Form.Control
    type="password"
    placeholder="Enter new password to change"
    value={newPassword}  // Use separate state, don't populate from config
    onChange={e => setNewPassword(e.target.value)}
/>
```

---

##  MEDIUM PRIORITY (Post-Launch)

See `PRE_LAUNCH_QA_REPORT.md` for full list of medium priority issues.

---

## 📋 DEPLOYMENT CHECKLIST

Before deploying to production:

### Environment Variables
- [ ] Set `SESSION_KEY` environment variable
  ```bash
  SESSION_KEY=$(openssl rand -hex 32)
  ```
- [ ] Set `SECURE_COOKIES=true` for HTTPS deployments
- [ ] Verify `FRONTEND_BACKEND_API_KEY` is set
- [ ] Verify `BACKEND_URL` is set correctly

### Testing
- [ ] Run `npm audit fix` and verify no critical vulnerabilities
- [ ] Test settings save/load cycle
- [ ] Test error messages are displayed when settings fail to save
- [ ] Test session persistence after container restart (with SESSION_KEY set)
- [ ] Test WebDAV authentication with password
- [ ] Test onboarding flow end-to-end
- [ ] Test on mobile device (< 900px width)

### Documentation
- [ ] Update README with SESSION_KEY requirement
- [ ] Update docker-compose.yaml example with SESSION_KEY
- [ ] Document error handling improvements

---

## Files Modified

1. ✅ `frontend/app/auth/authentication.server.ts` - Added SESSION_KEY warning
2. ✅ `frontend/app/routes/settings.update/route.tsx` - Added error handling
3. ✅ `PRE_LAUNCH_QA_REPORT.md` - Created comprehensive audit report
4. ✅ `CRITICAL_FIXES_APPLIED.md` - This file

---

## Next Steps

1. **Immediately:**
   - Run `npm audit fix`
   - Fix CRIT-3 (settings error display)
   - Fix CRIT-5 & CRIT-6 (password validation/hashing)

2. **Before Launch:**
   - Address all remaining CRITICAL issues
   - Fix all HIGH priority issues
   - Test thoroughly

3. **Launch Week:**
   - Set up error monitoring
   - Monitor user feedback
   - Address MEDIUM priority issues as needed

---

**Report Generated:** 2025-11-22
**Status:** ⚠️ CRITICAL WORK REQUIRED BEFORE LAUNCH
