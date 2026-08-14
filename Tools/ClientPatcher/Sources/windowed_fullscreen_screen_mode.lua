-- Replacement for MakeScreenModeControl in the r208022 x2ui option module.
-- The compiled prototype is transplanted into screen_option.alb by
-- Build-WindowedFullscreenScreenOption.ps1.

local function MakeScreenModeControl(frame)
  local screenModeTexts = {
    locale.optionWindow.screen.basicOptionItemScreenMode[1],
    locale.optionWindow.screen.basicOptionItemScreenMode[2],
    "Windowed Fullscreen"
  }
  local control = frame:InsertNewOption(
    "radiobuttonV",
    locale.optionWindow.screen.basicOptionItems.screenMode,
    screenModeTexts
  )

  function control:Init()
    local fullscreen = GetOptionItemValue(OPTION_ITEM_FULLSCREEN) == 1
    local borderless = GetOptionItemValue("r_FullscreenWindow") == 1

    if fullscreen then
      self:Check(1, false)
    elseif borderless then
      self:Check(3, false)
    else
      self:Check(2, false)
    end
  end

  function control:Save()
    local selected = self:GetChecked()
    local borderless = selected == 3 and 1 or 0
    SetOptionItemValue("r_FullscreenWindow", borderless)

    if borderless == 1 then
      SetOptionItemValue("r_windowx", 0)
      SetOptionItemValue("r_windowy", 0)
    end

    if selected == 1 then
      SetOptionItemValue(OPTION_ITEM_FULLSCREEN, 1)
    else
      SetOptionItemValue(OPTION_ITEM_FULLSCREEN, 0)
    end
  end
end
