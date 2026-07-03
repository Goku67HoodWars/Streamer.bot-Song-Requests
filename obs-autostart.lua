-- ============================================================================
--  Song Requests - OBS auto-start + audio source
--  ONE script that does the whole OBS side:
--    * starts the request engine when OBS launches, stops it when OBS closes
--    * makes sure the "YouTube Player" audio source exists, with the correct
--      audio routing (Control audio via OBS, monitoring OFF) - no manual source,
--      no audio-settings fiddling
--
--  SET UP ONCE:
--    OBS  ->  Tools  ->  Scripts  ->  the  +  button  ->  pick this file.
--  That's it. The engine runs whenever OBS is open, the audio source is created
--  for you, and the dock controls everything else. (Streamer.bot still needs to
--  be running for the channel-point redemption feed.)
-- ============================================================================

obs = obslua

local SOURCE_NAME = "YouTube Player"
local SOURCE_URL  = "http://127.0.0.1:8090/youtube"

local function exe_path()
  local appdata = os.getenv("LOCALAPPDATA")
  if not appdata then return nil end
  local p = appdata .. "\\SongRequests\\SongRequests.exe"
  local f = io.open(p, "r")
  if not f then return nil end          -- app not installed in the standard location
  f:close()
  return p
end

local function run(args)
  local p = exe_path()
  if not p then return end
  -- launch detached so OBS never waits on it; the engine is a windowless app
  os.execute('start "" "' .. p .. '" ' .. args)
end

-- Is there ALREADY a browser source pointing at the player page - whatever it's called? Streamers who
-- set up by hand named theirs "Youtube Player", "YT audio", anything - and a name-only check (which is
-- also case-SENSITIVE) would happily create a DUPLICATE that plays every song twice. The URL is the
-- thing that actually matters, so the URL is the key.
local function player_source_exists()
  local found = false
  local sources = obs.obs_enum_sources()
  if sources ~= nil then
    for _, src in ipairs(sources) do
      if obs.obs_source_get_unversioned_id(src) == "browser_source" then
        local st = obs.obs_source_get_settings(src)
        local url = obs.obs_data_get_string(st, "url")
        obs.obs_data_release(st)
        if url ~= nil and string.find(string.lower(url), "127.0.0.1:8090/youtube", 1, true) then found = true end
      end
    end
    obs.source_list_release(sources)
  end
  return found
end

-- Create the audio browser source (once) with the right routing, and drop it in the current scene.
-- Idempotent: if ANY source already points at the player page we leave the user's setup completely
-- alone. If you delete it and don't want it back, remove this script - it only exists because of this.
local function ensure_source()
  local existing = obs.obs_get_source_by_name(SOURCE_NAME)
  if existing ~= nil then
    obs.obs_source_release(existing)
    return                                  -- already there -> don't touch the user's setup
  end
  if player_source_exists() then return end  -- a hand-made player source (any name) counts too

  local scene_source = obs.obs_frontend_get_current_scene()
  if scene_source == nil then return end    -- scenes not ready yet; the finished-loading event retries
  local scene = obs.obs_scene_from_source(scene_source)
  if scene == nil then obs.obs_source_release(scene_source); return end

  local settings = obs.obs_data_create()
  obs.obs_data_set_string(settings, "url", SOURCE_URL)
  obs.obs_data_set_int(settings, "width", 1280)
  obs.obs_data_set_int(settings, "height", 720)
  obs.obs_data_set_bool(settings, "reroute_audio", true)   -- "Control audio via OBS": audio through the OBS mixer
  local source = obs.obs_source_create("browser_source", SOURCE_NAME, settings, nil)
  obs.obs_data_release(settings)

  if source ~= nil then
    -- monitoring OFF: Monitor+Output would double-capture via Desktop Audio and echo
    obs.obs_source_set_monitoring_type(source, obs.OBS_MONITORING_TYPE_NONE)
    obs.obs_scene_add(scene, source)
    obs.obs_source_release(source)
  end
  obs.obs_source_release(scene_source)
end

local function on_event(event)
  -- FINISHED_LOADING: scenes are fully loaded (covers the OBS-startup case where script_load runs too
  -- early). SCENE_COLLECTION_CHANGED: sources are PER-COLLECTION, so a collection the user switches to
  -- (or creates) mid-session needs its own copy of the audio source too.
  if event == obs.OBS_FRONTEND_EVENT_FINISHED_LOADING
     or event == obs.OBS_FRONTEND_EVENT_SCENE_COLLECTION_CHANGED then
    ensure_source()
  end
end

function script_description()
  return [[<b>Song Requests - auto-start + audio source</b><br/>
Starts the request engine when OBS launches (stops it when OBS closes) and creates
the "YouTube Player" audio source for you, wired to play through OBS. The dock
controls everything else. (Streamer.bot must still be running for redemptions.)]]
end

-- runs when OBS loads the script (on OBS startup, and right now when you add it)
function script_load(settings)
  run("--engine")
  obs.obs_frontend_add_event_callback(on_event)
  ensure_source()   -- if you added the script mid-session, scenes are already up -> create it now
end

-- runs when OBS closes (or the script is removed) -> shut the engine down
function script_unload()
  obs.obs_frontend_remove_event_callback(on_event)
  run("--stop")
end
