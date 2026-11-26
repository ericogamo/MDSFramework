namespace MDSFramework.Api
{
    public enum FactionAffinity
    {
        Any = 0,
        Boscali = 1,
        Primeva = 2
    }

    public enum WeaponCascadeRole
    {
        FireControl = 0,
        LaserHel = 1,
        CloseInGun = 2,
        ShortMedSam = 3,
        LongRangeSam = 4,
        AbmVls = 5,
        EarlyWarningRadar = 6,
        CommandControl = 7,
        AntiShipCoastal = 8,
        LogisticsHauler = 9,
        AntiVehicleDrone = 10
    }

    public enum SensorBand
    {
        XBand = 0,
        SBand = 1,
        VhfLongWave = 2,
        EoIr = 3,
        PassiveElint = 4
    }

    public enum MissileFlightProfile
    {
        Direct = 0,
        Lofting = 1,
        SeaSkimming = 2,
        Ballistic = 3,
        MarvTerminal = 4,
        Decoy = 5,
        SeaSkimmingEvasive = 6,
        LoiteringTopAttack = 7
    }
}
