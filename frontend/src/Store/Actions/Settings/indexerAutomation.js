import { createAction } from 'redux-actions';
import createFetchHandler from 'Store/Actions/Creators/createFetchHandler';
import createSaveHandler from 'Store/Actions/Creators/createSaveHandler';
import createSetSettingValueReducer from 'Store/Actions/Creators/Reducers/createSetSettingValueReducer';
import { createThunk } from 'Store/thunks';

//
// Variables

const section = 'settings.indexerAutomation';

//
// Actions Types

export const FETCH_INDEXER_AUTOMATION_SETTINGS = 'settings/indexerAutomation/fetchIndexerAutomationSettings';
export const SET_INDEXER_AUTOMATION_SETTINGS_VALUE = 'settings/indexerAutomation/setIndexerAutomationSettingsValue';
export const SAVE_INDEXER_AUTOMATION_SETTINGS = 'settings/indexerAutomation/saveIndexerAutomationSettings';

//
// Action Creators

export const fetchIndexerAutomationSettings = createThunk(FETCH_INDEXER_AUTOMATION_SETTINGS);
export const saveIndexerAutomationSettings = createThunk(SAVE_INDEXER_AUTOMATION_SETTINGS);
export const setIndexerAutomationSettingsValue = createAction(SET_INDEXER_AUTOMATION_SETTINGS_VALUE, (payload) => {
  return {
    section,
    ...payload
  };
});

//
// Details

export default {

  //
  // State

  defaultState: {
    isFetching: false,
    isPopulated: false,
    error: null,
    pendingChanges: {},
    isSaving: false,
    saveError: null,
    item: {}
  },

  //
  // Action Handlers

  actionHandlers: {
    [FETCH_INDEXER_AUTOMATION_SETTINGS]: createFetchHandler(section, '/indexer/automation'),
    [SAVE_INDEXER_AUTOMATION_SETTINGS]: createSaveHandler(section, '/indexer/automation')
  },

  //
  // Reducers

  reducers: {
    [SET_INDEXER_AUTOMATION_SETTINGS_VALUE]: createSetSettingValueReducer(section)
  }

};
