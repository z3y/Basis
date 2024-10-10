[System.Serializable]
public struct BasisBundleInformation
{
    public BasisBundleDescription BasisBundleDescription;
    public BasisBundleGenerated BasisBundleGenerated;
}
[System.Serializable]
public struct BasisBundleDescription
{
    public string AssetBundleName;//user friendly name of this asset.
    public string AssetBundleDescription;//the description of this asset
}
[System.Serializable]
public struct BasisBundleGenerated
{
    public string AssetBundleHash;//hash stored seperately
    public string AssetMode;//Scene or Gameobject
    public string AssetToLoadName;// assets name we are using out of the box.
}