namespace OnaPlotter.Services;

/// <summary>
/// Every <see cref="IKeyValueStore"/> key the settings services persist,
/// declared once so Load and Save sites cannot drift apart.
/// <para>
/// Naming: "&lt;camelCaseName&gt;.vN", where N is the schema version of the
/// stored value. Bump N on a breaking format change (bool to enum string,
/// scalar to struct) so a stale value reads as "missing, use default"
/// instead of a parse failure. A renamed key keeps its old spelling as a
/// <c>Legacy*</c> constant and gets a row in <see cref="Renamed"/>.
/// </para>
/// </summary>
public static class SettingKeys
{
    public const string NightMode = "nightMode.v1";
    public const string NightModeAuto = "nightModeAuto.v1";
    public const string LastManualNightToggle = "lastManualNightToggle.v1";
    public const string LastManualNightOverrideSunCluster = "lastManualNightOverrideSunCluster.v1";
    public const string ChartsSeeded = "chartsSeeded.v1";
    public const string Theme = "theme.v1";
    public const string WindHeroMode = "windHeroMode.v1";
    public const string MapOrientation = "mapOrientation.v1";
    public const string ShipOrientationSource = "shipOrientationSource.v1";
    public const string FollowBoat = "followBoat.v1";
    public const string LaylinesVisible = "laylinesVisible.v1";
    public const string ShipLinesVisible = "shipLinesVisible.v1";
    public const string ServerTrackVisible = "serverTrackVisible.v1";
    public const string ServerTrackDuration = "serverTrackDuration.v1";
    public const string ServerTrackResolution = "serverTrackResolution.v1";
    public const string ServerTrackWithinBounds = "serverTrackWithinBounds.v1";
    public const string AtonsVisible = "atonsVisible.v1";
    public const string AisLabelsVisible = "aisLabelsVisible.v1";
    public const string WeatherOverlayOpacity = "weatherOverlayOpacity.v1";
    public const string ChartContrastPercent = "chartContrastPercent.v1";
    public const string ChartSaturationPercent = "chartSaturationPercent.v1";
    public const string ChartBrightnessPercent = "chartBrightnessPercent.v1";
    public const string ChartUpscaleEnabled = "chartUpscaleEnabled.v1";
    public const string ChartUpscaleLevels = "chartUpscaleLevels.v1";
    public const string SidebarCollapsed = "sidebarCollapsed.v1";
    public const string DepthAlarmThreshold = "depthAlarmThreshold.v1";
    public const string CpaAlarmNm = "cpaAlarmNm.v1";
    public const string TcpaAlarmMin = "tcpaAlarmMin.v1";
    public const string CpaAwarenessNm = "cpaAwarenessNm.v1";
    public const string TcpaAwarenessMin = "tcpaAwarenessMin.v1";
    public const string CpaDebounceSeconds = "cpaDebounceSeconds.v1";
    public const string WindShiftAlarmThreshold = "windShiftAlarmThreshold.v1";
    public const string WindShiftLookbackMinutes = "windShiftLookbackMinutes.v1";
    public const string WindShiftMinTrueWindSpeed = "windShiftMinTrueWindSpeed.v1";
    public const string AnchorTideSafetyMargin = "anchorTideSafetyMargin.v1";
    public const string AnchorAutoRadiusSafetyMargin = "anchorAutoRadiusSafetyMargin.v1";
    public const string ManualAnchorRadiusMeters = "manualAnchorRadiusMeters.v1";
    public const string AnchorChainLengthMeters = "anchorChainLengthMeters.v1";
    public const string DeadmanTimeoutMinutes = "deadmanTimeoutMinutes.v1";
    public const string DeadmanNightMinutes = "deadmanNightMinutes.v1";
    public const string SnoozeDurationMinutes = "snoozeDurationMinutes.v1";
    public const string BigType = "bigType.v1";
    public const string ExpandAllHud = "expandAllHud.v1";
    public const string SailingMode = "sailingMode.v1";
    public const string OwnVesselType = "ownVesselType.v1";
    public const string KeepScreenAwake = "keepScreenAwake.v1";
    public const string ServerSideApproachAlarms = "serverSideApproachAlarms.v1";
    public const string ShowKeyboardHints = "showKeyboardHints.v1";
    public const string ShowAutopilotHud = "showAutopilotHud.v1";
    public const string ShowRadarHud = "showRadarHud.v1";
    public const string ShowDefaultHud = "showDefaultHud.v1";
    public const string OwnCogVectorMinutes = "ownCogVectorMinutes.v1";
    public const string AisCogVectorMinutes = "aisCogVectorMinutes.v1";
    public const string AisInactiveMinutes = "aisInactiveMinutes.v1";
    public const string AisRemoveMinutes = "aisRemoveMinutes.v1";
    public const string TideVisible = "tideVisible.v1";
    public const string RadarRangeRingsEnabled = "radarRangeRingsEnabled.v1";
    public const string RadarRangeRingsCount = "radarRangeRingsCount.v1";
    public const string DistanceRingsEnabled = "distanceRingsEnabled.v1";
    public const string DistanceRingsBaseNm = "distanceRingsBaseNm.v1";
    public const string DistanceRingsCount = "distanceRingsCount.v1";
    public const string RadarUseWireBearing = "radarUseWireBearing.v1";
    public const string RadarBearingCorrection = "radarBearingCorrection.v1";
    public const string PreferMagneticHeading = "preferMagneticHeading.v1";
    public const string CogReadoutSource = "cogReadoutSource.v1";
    public const string OwnCogVectorSource = "ownCogVectorSource.v1";
    public const string AutoAdvanceWaypoints = "autoAdvanceWaypoints.v1";
    public const string ArrivalCircleMeters = "arrivalCircleMeters.v1";
    public const string MarinePoiFuel = "marinePoi.fuel.v1";
    public const string MarinePoiMarina = "marinePoi.marina.v1";
    public const string MarinePoiHarbour = "marinePoi.harbour.v1";
    public const string MarinePoiMooring = "marinePoi.mooring.v1";
    public const string MarinePoiSlipway = "marinePoi.slipway.v1";
    public const string MarinePoiPier = "marinePoi.pier.v1";
    public const string MarinePoiChandlery = "marinePoi.chandlery.v1";
    public const string MarinePoiDrinkingWater = "marinePoi.drinkingWater.v1";
    public const string MarinePoiPumpOut = "marinePoi.pumpOut.v1";
    public const string EnabledChartIds = "enabledChartIds.v1";
    public const string EnabledRouteIds = "enabledRouteIds.v1";
    public const string ChartOrder = "chartOrder.v1";
    public const string DisabledAlarmRules = "disabledAlarmRules.v1";
    public const string MapView = "mapView.v1";
    public const string QuickBarChartIds = "quickBarChartIds.v1";
    public const string StandaloneMode = "standaloneMode.v1";
    public const string StandaloneServerUrl = "standaloneServerUrl.v1";
    public const string RememberSession = "rememberSession.v1";
    public const string RememberPassword = "rememberPassword.v1";
    public const string StandaloneUsername = "standaloneUsername.v1";
    public const string StandalonePassword = "standalonePassword.v1";
    public const string Polar = "polar.v1";

    // Pre-convention spellings. Each is moved to its ".v1" successor on
    // first load (Renamed below; the polar pair by PolarService).
    public const string LegacyNightMode = "nightMode";
    public const string LegacyTheme = "theme";
    public const string LegacyMapOrientation = "mapOrientation";
    public const string LegacyFollowBoat = "followBoat";
    public const string LegacyLaylinesVisible = "laylinesVisible";
    public const string LegacyDepthAlarmThreshold = "depthAlarmThreshold";
    public const string LegacyWindShiftAlarmThreshold = "windShiftAlarmThreshold";
    public const string LegacyWindShiftLookbackMinutes = "windShiftLookbackMinutes";
    public const string LegacyAnchorTideSafetyMargin = "anchorTideSafetyMargin";
    public const string LegacySailingMode = "sailingMode";
    public const string LegacyEnabledChartIds = "enabledChartIds";
    public const string LegacyEnabledRouteIds = "enabledRouteIds";
    public const string LegacyPolar = "polar";

    // Older keys read once as a fallback seed for a successor setting
    // whose value format differs; never written.
    public const string LegacyCpaAlarmThreshold = "cpaAlarmThreshold";
    public const string LegacyGuardZoneLookaheadMinutes = "guardZoneLookaheadMinutes";
    public const string LegacyPreferMagneticCourse = "preferMagneticCourse.v1";

    /// <summary>
    /// (legacy, current) pairs migrated by <see cref="AppSettingsService"/>
    /// before its first load. Same value format on both sides; only the
    /// key spelling changed.
    /// </summary>
    public static readonly IReadOnlyList<(string Legacy, string Current)> Renamed =
    [
        (LegacyNightMode, NightMode),
        (LegacyTheme, Theme),
        (LegacyMapOrientation, MapOrientation),
        (LegacyFollowBoat, FollowBoat),
        (LegacyLaylinesVisible, LaylinesVisible),
        (LegacyDepthAlarmThreshold, DepthAlarmThreshold),
        (LegacyWindShiftAlarmThreshold, WindShiftAlarmThreshold),
        (LegacyWindShiftLookbackMinutes, WindShiftLookbackMinutes),
        (LegacyAnchorTideSafetyMargin, AnchorTideSafetyMargin),
        (LegacySailingMode, SailingMode),
        (LegacyEnabledChartIds, EnabledChartIds),
        (LegacyEnabledRouteIds, EnabledRouteIds),
    ];
}
