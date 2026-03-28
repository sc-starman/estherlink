# Backend Website Localization Index (EN/RU)

This file tracks localized UI coverage for the backend website (`src/OmniRelay.Backend`) and the shared client-side dashboard script.

## Routes and key groups

### Public routes
- `/`:
  - `Index.*`
  - `Index.Config.*`
- `/docs`:
  - `Docs.*`
- `/download`:
  - `Download.*`
- `/contact`:
  - `Contact.*`

### Account routes
- `/account/login`:
  - `Account.Login.*`
  - `Login.Msg.*`
  - `Common.*`
- `/account/register`:
  - `Account.Register.*`
  - `Register.Msg.*`
  - `Register.Email.*`
  - `Common.*`
- `/account/confirm-email`:
  - `Account.ConfirmEmail.*`
  - `ConfirmEmail.Msg.*`
- `/account/access-denied`:
  - `Account.AccessDenied.*`
- `/account/logout`:
  - layout/global keys only

### Dashboard / app routes
- `/dashboard`:
  - `Dashboard.*`
- `/dashboard/trial`:
  - `Trial.*`
- `/dashboard/licenses`:
  - `Licenses.*`
- `/dashboard/billing`:
  - `Billing.*`
  - `Js.*` (for `dashboard.js` messages)
- `/app/account`:
  - `AppAccount.*`
- `/app/downloads`:
  - `AppDownloads.*`

### Admin routes
- `/admin`:
  - `Admin.Title`
  - `Admin.Subtitle`
  - `Admin.Card.*`
- `/admin/trials`:
  - `Admin.Trials.*`
  - `Admin.Table.*`
- `/admin/paid-licenses`:
  - `Admin.Paid.*`
  - `Admin.Table.*`
- `/admin/billing`:
  - `Admin.Billing.*`
- `/admin/discounts`:
  - `Admin.Discounts.*`

### Shared partials / layout
- Global layout + header/footer:
  - `Layout.*`
  - `Common.*`
- Dashboard sidebar:
  - `Dashboard.Sidebar.*`
- Admin sidebar:
  - `Admin.Sidebar.*`

## Client-side i18n dictionary
- Injected by: `Pages/Shared/_Layout.cshtml` (`window.omniRelayI18n`)
- Consumed by: `wwwroot/js/dashboard.js`
- Key namespace: `Js.*`

## Resource files
- `src/OmniRelay.Backend/Resources/Localization.SharedResource.en.resx`
- `src/OmniRelay.Backend/Resources/Localization.SharedResource.ru.resx`
