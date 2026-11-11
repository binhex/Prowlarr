import PropTypes from 'prop-types';
import React from 'react';
import FieldSet from 'Components/FieldSet';
import Button from 'Components/Link/Button';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { kinds } from 'Helpers/Props';
import formatDateTime from 'Utilities/Date/formatDateTime';
import translate from 'Utilities/String/translate';

function formatDate(value, dateFormat, timeFormat) {
  if (!value) {
    return '-';
  }

  // UI settings load asynchronously; fall back to the runtime locale until they arrive.
  if (!dateFormat || !timeFormat) {
    return new Date(value).toLocaleString();
  }

  return formatDateTime(value, dateFormat, timeFormat);
}

function IndexerAutomationStateTable(props) {
  const {
    items,
    dateFormat,
    timeFormat,
    onClearSuppressionPress
  } = props;

  return (
    <FieldSet legend={translate('IndexerAutomationState')}>
      {
        !items.length &&
          <div>{translate('IndexerAutomationNoState')}</div>
      }

      {
        !!items.length &&
          <Table
            horizontalScroll={true}
            selectAll={false}
            columns={[
              { name: 'name', label: () => translate('Name'), isVisible: true },
              { name: 'lastAttemptResult', label: () => translate('IndexerAutomationLastResult'), isVisible: true },
              { name: 'failureSince', label: () => translate('IndexerAutomationFailureSince'), isVisible: true },
              { name: 'nextAttemptAt', label: () => translate('IndexerAutomationNextAttempt'), isVisible: true },
              { name: 'suppressed', label: () => translate('IndexerAutomationSuppressed'), isVisible: true }
            ]}
          >
            <TableBody>
              {
                items.map((item) => {
                  return (
                    <TableRow key={item.key}>
                      <TableRowCell>{item.name}</TableRowCell>
                      <TableRowCell>{item.lastAttemptResult || '-'}</TableRowCell>
                      <TableRowCell>{formatDate(item.failureSince, dateFormat, timeFormat)}</TableRowCell>
                      <TableRowCell>{formatDate(item.nextAttemptAt, dateFormat, timeFormat)}</TableRowCell>
                      <TableRowCell>
                        {
                          item.suppressed &&
                            <Button
                              kind={kinds.DEFAULT}
                              onPress={() => onClearSuppressionPress(item.key)}
                            >
                              {translate('IndexerAutomationClearSuppression')}
                            </Button>
                        }
                        {
                          !item.suppressed && '-'
                        }
                      </TableRowCell>
                    </TableRow>
                  );
                })
              }
            </TableBody>
          </Table>
      }
    </FieldSet>
  );
}

IndexerAutomationStateTable.propTypes = {
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  dateFormat: PropTypes.string,
  timeFormat: PropTypes.string,
  onClearSuppressionPress: PropTypes.func.isRequired
};

export default IndexerAutomationStateTable;
