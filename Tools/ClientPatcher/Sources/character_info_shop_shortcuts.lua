-- ArcheAge r208022 Character Info currency-shop shortcuts.
-- Signals are handled by CurrencyShopManager on the AAEmu game server.

local HONOR_SHOP_SIGNAL = 100
local VOCATION_SHOP_SIGNAL = 101
local SHOP_BUTTON_SIZE = 20

local originalCreateHonorPointLabel = CreateHonorPointLabel
local originalCreateLivingPointLabel = CreateLivingPointLabel

local function AddCurrencyShopButton(owner, id, tooltipText, signal)
  local button = CreateEmptyButton(id, owner)
  button:SetExtent(SHOP_BUTTON_SIZE, SHOP_BUTTON_SIZE)
  button:AddAnchor("RIGHT", owner, "RIGHT", -1, 0)
  ApplyButtonSkin(button, BUTTON_CONTENTS.CHARACTER_INFO_DETAIL)

  function button:OnClick(arg)
    if arg == "LeftButton" then
      X2Chat:ExpressEmotion(signal)
    end
  end
  button:SetHandler("OnClick", button.OnClick)

  function button:OnEnter()
    SetTooltip(tooltipText, self, true, true)
  end
  button:SetHandler("OnEnter", button.OnEnter)

  function button:OnLeave()
    HideTooltip()
  end
  button:SetHandler("OnLeave", button.OnLeave)

  owner.shopShortcutButton = button
end

function CreateHonorPointLabel(parent, width, height)
  local widget = originalCreateHonorPointLabel(parent, width, height)
  AddCurrencyShopButton(widget, "character.window.honorShopShortcut", "Open Honor Shop", HONOR_SHOP_SIGNAL)
  return widget
end

function CreateLivingPointLabel(parent, width, height)
  local widget = originalCreateLivingPointLabel(parent, width, height)
  AddCurrencyShopButton(widget, "character.window.vocationShopShortcut", "Open Vocation Shop", VOCATION_SHOP_SIGNAL)
  return widget
end
