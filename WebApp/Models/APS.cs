using System;
using Autodesk.Authentication.Model;
using System.Collections.Generic;

public class Tokens
{
    public string InternalToken;
    public string PublicToken;
    public string RefreshToken;
    public DateTime ExpiresAt;
}

public class AutomationSettings
{
    // Folder with one subfolder per Revit version, each holding the app bundle zip built by the RevitPlugin project
    // (RevitPlugin/bin/Release/<version>/ExchangeViewCreator.zip)
    public string BundlesPath;
    // Designs saved with an older Revit version are upgraded at least to this one. Exchanges created from Revit 2024
    // files never completed in our tests (see bug_report/README.md), while Revit 2025 files gave correct exchanges.
    public int MinRevitVersion = 2025;
}

public partial class APS
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _callbackUri;
    private readonly AutomationSettings _automationSettings;
    public int MinRevitVersion => _automationSettings.MinRevitVersion;
    // Reading and writing files in ACC, and creating exchanges
    private readonly List<Scopes> InternalTokenScopes = [Scopes.DataRead, Scopes.DataWrite, Scopes.DataCreate, Scopes.ViewablesRead];
    private readonly List<Scopes> PublicTokenScopes = [Scopes.ViewablesRead];
    // Design Automation, and the app-owned bucket where the selection sets are stored
    private readonly List<Scopes> AppTokenScopes = [Scopes.CodeAll, Scopes.BucketCreate, Scopes.BucketRead, Scopes.DataRead, Scopes.DataWrite, Scopes.DataCreate];

    public APS(string clientId, string clientSecret, string callbackUri, AutomationSettings automationSettings)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _callbackUri = callbackUri;
        _automationSettings = automationSettings;
    }
}
