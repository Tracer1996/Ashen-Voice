# Discord setup for Phase 3.1

Only the project owner does these steps. Regular Ashen Voice users will only click **Connect Discord**.

## 1. Create the Discord application

1. Open the Discord Developer Portal.
2. Click **New Application**.
3. Name it `Ashen Voice`.
4. Open **General Information**.
5. Copy the **Application ID**.

Do not create a bot. Do not copy a bot token. Do not put a client secret in the repository.

## 2. Add the local redirect URI

1. Open the application's **OAuth2** page.
2. Under **Redirects**, add this exact address:

```text
http://127.0.0.1:53682/callback/
```

3. Save changes.

## 3. Add development testers

1. Open **App Testers** in the application's left menu.
2. Add each Discord account that will test Ashen Voice.
3. Each tester must accept the invitation sent by Discord.

The application owner should already have development access. Public distribution of the RPC voice scopes will require Discord approval later.

## 4. Add the GitHub repository variable

1. Open the `Tracer1996/Ashen-Voice` repository on GitHub.
2. Click **Settings**.
3. Click **Secrets and variables**.
4. Click **Actions**.
5. Open the **Variables** tab.
6. Click **New repository variable**.
7. Enter:

```text
Name: DISCORD_CLIENT_ID
Value: paste the Discord Application ID
```

8. Click **Add variable**.

The Application ID is public and is compiled into Ashen Voice. Never add a bot token or client secret.

## 5. Upload and build

Copy the Phase 3.1 repository update into your existing repository, commit, and push it. Then run:

```text
Actions → Build Ashen Voice Phase 3.1 Local Discord → Run workflow
```

Download the installer from the completed run's **Artifacts** section.

## Expected first connection

1. Discord desktop must be open.
2. Click **Connect Discord** in Ashen Voice.
3. The browser opens Discord authorization.
4. Approve Ashen Voice.
5. Return to the app.

Ashen Voice should show:

```text
Discord — Connected
Connected as <your display name>
Voice: <server> — <channel>
```

If Discord returns an approval or scope error, confirm the account is the application owner or an accepted App Tester. That error also confirms that Discord is enforcing RPC access restrictions; it is not fixed by a bot token.
