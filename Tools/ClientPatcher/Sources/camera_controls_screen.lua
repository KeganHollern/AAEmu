-- Adds persistent camera-distance and field-of-view controls to the basic
-- screen-options page in ArcheAge r208022. Build-WindowedFullscreenScreenOption.ps1
-- transplants the callbacks and replacement frame prototype into screen_option.alb.

local makeResolutionControl
local makeScreenModeControl
local makeGammaControl
local makeQualityControl
local makeRenderThreadControl
local makeUiScaleControl

function AAEmuApplyCameraMaxDistance(value)
  value = tonumber(value) or 35
  value = math.max(10, math.min(35, math.floor(value + 0.5)))
  if Console ~= nil then
    Console:ExecuteString("camera_max_dist " .. tostring(value))
  end
end

function AAEmuApplyCameraFov(value)
  value = tonumber(value) or 60
  value = math.max(40, math.min(120, value))
  if Console ~= nil then
    Console:ExecuteString("cl_fov " .. tostring(value))
  end
end

function AAEmuDefaultCameraFov()
  local cameraMode = 1
  if OPTION_ITEM_CAMERA_FOV_SET ~= nil then
    cameraMode = GetOptionItemValue(OPTION_ITEM_CAMERA_FOV_SET) or 1
  end
  if cameraMode == 2 then
    return 42.75
  end
  return 60
end

function AAEmuApplyPersistedCameraOptions()
  if GetOptionItemValue == nil then
    return
  end

  AAEmuApplyCameraMaxDistance(GetOptionItemValue("AAEmuCameraMaxDistance"))
  AAEmuApplyCameraFov(GetOptionItemValue("AAEmuCameraFov"))
end

return function(parent, id)
  local frame = CreateOptionSubFrame(parent, id)
  local tooltipTexts = {}
  local tooltipIndex = 1

  local function appendStockTooltip()
    local stockTooltips = locale.optionWindow.screen.basicOptionItems_tooltip
    if tooltipIndex > #stockTooltips then
      return
    end
    tooltipTexts[#tooltipTexts + 1] = stockTooltips[tooltipIndex]
    tooltipIndex = tooltipIndex + 1
  end

  makeResolutionControl(frame)
  appendStockTooltip()

  makeScreenModeControl(frame)
  appendStockTooltip()

  frame:InsertNewOption(
    "checkbox",
    locale.optionWindow.screen.basicOptionItems.synVertical,
    locale.optionWindow.screen.fullScreen,
    OPTION_ITEM_VSYNC
  )
  appendStockTooltip()

  makeGammaControl(frame)
  appendStockTooltip()

  makeQualityControl(frame)
  appendStockTooltip()

  makeRenderThreadControl(frame)
  appendStockTooltip()

  if X2:IsEnteredWorld() then
    frame:InsertNewOption(OPTION_PARTITION_LINE)
    MakeCameraFovSetControl(frame)
    appendStockTooltip()

    frame:InsertNewOption(OPTION_PARTITION_LINE)

    local distanceSlider = frame:InsertNewOption(
      "sliderbar",
      "Maximum Camera Distance",
      { "10", "18", "26", "35" },
      "AAEmuCameraMaxDistance",
      AAEmuApplyCameraMaxDistance
    )
    distanceSlider:SetMinMaxValues(10, 35)
    distanceSlider:SetValueStep(1)
    tooltipTexts[#tooltipTexts + 1] =
      "Limits how far the third-person camera can zoom out. Lower values prevent large jumps when scrolling quickly."

    local fovSlider = frame:InsertNewOption(
      "sliderbar",
      "Field of View",
      { "40", "67", "93", "120" },
      "AAEmuCameraFov",
      AAEmuApplyCameraFov
    )
    fovSlider:SetMinMaxValues(40, 120)
    fovSlider:SetValueStep(1)
    tooltipTexts[#tooltipTexts + 1] =
      "Adjusts the camera field of view between 40 and 120 degrees."
  else
    tooltipIndex = tooltipIndex + 1
  end

  makeUiScaleControl(frame)
  appendStockTooltip()

  frame:SetTooltipTexts(tooltipTexts)
  return frame
end
