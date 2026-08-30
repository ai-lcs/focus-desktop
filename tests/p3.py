p = r'D:/focus-desktop/src/focus-desktop/MainWindow.xaml.cs'
src = open(p, encoding='utf-8').read()

SPK = "\U0001F50A"
SPK_MUTE = "\U0001F507"

old = '''    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeReady) VolumeHelper.Set((int)e.NewValue);
    }

    private bool _volumeReady;'''
new = '''    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeReady)
        {
            VolumeHelper.Set((int)e.NewValue);
            VolumePct.Text = ((int)e.NewValue).ToString();
            if (e.NewValue > 0) MuteButton.Content = SPK;
        }
    }

    /// <summary>静音/恢复（记住静音前音量）。</summary>
    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        var muted = VolumeHelper.ToggleMute();
        MuteButton.Content = muted ? SPK_MUTE : SPK;
    }

    private const string SPK = "\U0001F50A";
    private const string SPK_MUTE = "\U0001F507";

    private bool _volumeReady;'''
assert old in src, "volume block not found"
src = src.replace(old, new)

old = '''        VolumeHelper.Init();
        _volumeReady = true; // 先置位再设滑块值，避免构造期触发 Set
        VolumeSlider.Value = VolumeHelper.Get();'''
new = '''        VolumeHelper.Init();
        _volumeReady = true; // 先置位再设滑块值，避免构造期触发 Set
        VolumeSlider.Value = VolumeHelper.Get();
        VolumePct.Text = VolumeHelper.Get().ToString();
        MuteButton.Content = VolumeHelper.IsMuted() ? SPK_MUTE : SPK;'''
assert old in src
src = src.replace(old, new)

open(p, 'w', encoding='utf-8').write(src)
print("volume capsule wired")
