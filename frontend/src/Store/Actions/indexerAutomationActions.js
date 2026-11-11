import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { set } from './baseActions';
import createFetchHandler from './Creators/createFetchHandler';
import createHandleActions from './Creators/createHandleActions';

//
// Variables

export const section = 'indexerAutomation';

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  actionError: null,
  items: []
};

//
// Actions Types

export const FETCH_INDEXER_AUTOMATION_STATE = 'indexerAutomation/fetchIndexerAutomationState';
export const CLEAR_INDEXER_AUTOMATION_SUPPRESSION = 'indexerAutomation/clearIndexerAutomationSuppression';
export const RUN_INDEXER_AUTOMATION = 'indexerAutomation/runIndexerAutomation';

//
// Action Creators

export const fetchIndexerAutomationState = createThunk(FETCH_INDEXER_AUTOMATION_STATE);
export const clearIndexerAutomationSuppression = createThunk(CLEAR_INDEXER_AUTOMATION_SUPPRESSION);
export const runIndexerAutomation = createThunk(RUN_INDEXER_AUTOMATION);

//
// Action Handlers

export const actionHandlers = handleThunks({
  [FETCH_INDEXER_AUTOMATION_STATE]: createFetchHandler(section, '/indexer/automation/state'),
  [CLEAR_INDEXER_AUTOMATION_SUPPRESSION]: function(getState, payload, dispatch) {
    const promise = createAjaxRequest({
      url: `/indexer/automation/state/${encodeURIComponent(payload.key)}`,
      method: 'DELETE'
    }).request;

    promise.done(() => {
      dispatch(set({ section, actionError: null }));
      dispatch(fetchIndexerAutomationState());
    });

    promise.fail((xhr) => {
      dispatch(set({
        section,
        actionError: xhr
      }));
    });
  },
  [RUN_INDEXER_AUTOMATION]: function(getState, payload, dispatch) {
    const promise = createAjaxRequest({
      url: '/indexer/automation/run',
      method: 'POST'
    }).request;

    promise.done(() => {
      dispatch(set({ section, actionError: null }));
      dispatch(fetchIndexerAutomationState());
    });

    promise.fail((xhr) => {
      dispatch(set({
        section,
        actionError: xhr
      }));
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({}, defaultState, section);
