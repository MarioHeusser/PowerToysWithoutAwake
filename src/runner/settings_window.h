#pragma once
#include <optional>
#include <string>

enum class ESettingsWindowNames
{
    Overview = 0,
    ColorPicker,
    FancyZones,
    Run,
    ImageResizer,
    KBM,
    MouseUtils,
    PowerRename,
    FileExplorer,
    ShortcutGuide,
    VideoConference,
    Hosts,
    RegistryPreview,
};

std::string ESettingsWindowNames_to_string(ESettingsWindowNames value);
ESettingsWindowNames ESettingsWindowNames_from_string(std::string value);

void open_settings_window(std::optional<std::wstring> settings_window, bool show_flyout, const std::optional<POINT>& flyout_position);
void close_settings_window();

void open_oobe_window();
void open_scoobe_window();
void open_flyout();
