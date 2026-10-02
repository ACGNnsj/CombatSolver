"""Claim registered community batches and update only the entry body's owner cells.

Run through GitHub Actions, or locally with an authenticated gh CLI. Comments are
data: only an exact first-line claim/unclaim command can change its author's assignment.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys


REPOSITORY = 'Torch1230/CombatSolver'
ENTRY_ISSUE = 171
STATE_PATTERN = re.compile(r'<!-- combatsolver-community-claims: (\{[^\n]*\}) -->')
COMMAND_PATTERN = re.compile(r'(认领|取消认领|放弃认领)(?:\s*([BQ]\d{3}))?(?:\s*整批)?[。！!]?', re.IGNORECASE)
COMMAND_INTRO_PATTERN = re.compile(r'(认领|取消认领|放弃认领)(?:\s*([BQ]\d{3}))?\s*整批(?=$|[\s（(，,。.!！：:])', re.IGNORECASE)
ROW_PATTERN = re.compile(r'^\s*\|\s*\[([BQ]\d{3})\]\(https://github\.com/Torch1230/CombatSolver/issues/(\d+)\)\s*\|')


class GitHub:
    def request(self, route, method='GET', payload=None):
        args = ['gh', 'api', f'repos/{REPOSITORY}/{route}', '--method', method]
        if payload is not None:
            args += ['--input', '-']
        result = subprocess.run(args, input=None if payload is None else json.dumps(payload).encode(),
                                capture_output=True, check=True)
        return json.loads(result.stdout) if result.stdout else None

    def pages(self, route):
        result = []
        for page in range(1, 1001):
            items = self.request(f'{route}?per_page=100&page={page}')
            result.extend(items)
            if len(items) < 100:
                return result
        raise RuntimeError('GitHub pagination exceeded supported page count')


def parse_command(body, batch_id):
    first_line = body.strip().splitlines()[0] if body.strip() else ''
    if first_line == '/claim':
        return 'claim'
    if first_line == '/unclaim':
        return 'unclaim'
    match = COMMAND_PATTERN.fullmatch(first_line) or COMMAND_INTRO_PATTERN.match(first_line)
    if match is None or (match[2] is not None and match[2].upper() != batch_id):
        return None
    return 'claim' if match[1] == '认领' else 'unclaim'


def read_state(body):
    matches = list(STATE_PATTERN.finditer(body))
    if not matches:
        return {'schemaVersion': 1, 'lastCommentIds': {}}
    if len(matches) != 1:
        raise ValueError('Entry contains multiple claim state markers')
    state = json.loads(matches[0][1])
    if state['schemaVersion'] != 1 or not isinstance(state['lastCommentIds'], dict):
        raise ValueError('Unsupported claim state')
    if any(not k.isdecimal() or type(v) is not int or v < 0 for k, v in state['lastCommentIds'].items()):
        raise ValueError('Invalid claim comment cursor')
    return state


def owner_cell(assignees):
    if not assignees:
        return '未认领'
    names = []
    for user in assignees:
        login = user['login']
        if not re.fullmatch(r'[A-Za-z0-9-]+(?:\[bot\])?', login):
            raise ValueError('Invalid GitHub login')
        names.append(f'[{login}](https://github.com/{login})')
    return '、'.join(names)


def update_entry_body(body, batches, issues, state):
    """Keep prose and existing table cells; add/update owner cells in batch tables."""
    registered = {b['batchId']: b for b in batches}
    lines = body.splitlines(keepends=True)
    seen = set()
    row_tables = []
    for i, line in enumerate(lines):
        match = ROW_PATTERN.match(line)
        if match is None or match[1] not in registered:
            continue
        bid = match[1]
        if int(match[2]) != registered[bid]['issueNumber'] or bid in seen:
            raise ValueError('Entry batch link does not match publication ledger')
        seen.add(bid)
        header_index = i - 1
        while header_index >= 0 and lines[header_index].lstrip().startswith('|'):
            header_index -= 1
        row_tables.append((i, header_index + 1, registered[bid]))
    if seen != set(registered):
        raise ValueError('Entry table and publication ledger contain different batches')

    headers = {}
    for _, header_index, _ in row_tables:
        if header_index in headers:
            continue
        cells = [cell.strip() for cell in lines[header_index].strip().strip('|').split('|')]
        if cells[0] != '批次':
            raise ValueError('Unsupported batch table header')
        if '认领者' in cells:
            headers[header_index] = (cells.index('认领者'), len(cells), False)
        else:
            column = len(cells)
            headers[header_index] = (column, column + 1, True)
            for idx, value in [(header_index, '认领者'), (header_index + 1, '---')]:
                end = '\r\n' if lines[idx].endswith('\r\n') else '\n' if lines[idx].endswith('\n') else ''
                lines[idx] = lines[idx].rstrip('\r\n').rstrip() + ' ' + value + ' |' + end
    for i, header_index, batch in row_tables:
        column, width, appended = headers[header_index]
        cell = owner_cell(issues[batch['issueNumber']]['assignees'])
        end = '\r\n' if lines[i].endswith('\r\n') else '\n' if lines[i].endswith('\n') else ''
        line = lines[i].rstrip('\r\n')
        fields = line.split('|')
        if len(fields) - 2 != width - int(appended):
            raise ValueError('Unexpected batch row width')
        if appended:
            lines[i] = line.rstrip() + ' ' + cell + ' |' + end
        else:
            fields[column + 1] = ' ' + cell + ' '
            lines[i] = '|'.join(fields) + end
    updated = ''.join(lines)
    marker = '<!-- combatsolver-community-claims: ' + json.dumps(state, ensure_ascii=False, separators=(',', ':')) + ' -->'
    if STATE_PATTERN.search(updated):
        return STATE_PATTERN.sub(lambda _: marker, updated)
    return updated.rstrip('\r\n') + '\n\n' + marker + '\n'


def synchronize(api, batches, dry_run=False):
    entry = api.request(f'issues/{ENTRY_ISSUE}')
    state = read_state(entry['body'])
    cursors = dict(state['lastCommentIds'])
    issues = {b['issueNumber']: api.request(f"issues/{b['issueNumber']}") for b in batches}
    pending = []
    # Read all unseen comments, so a newer queued run also handles events it superseded.
    for batch in batches:
        number = batch['issueNumber']
        previous = cursors.get(str(number), 0)
        comments = api.pages(f'issues/{number}/comments')
        for comment in comments:
            if comment['id'] > previous:
                pending.append((comment['id'], batch, comment))
        cursors[str(number)] = max([previous] + [c['id'] for c in comments])
    actions = []
    for _, batch, comment in sorted(pending, key=lambda item: item[0]):
        number = batch['issueNumber']
        command = parse_command(comment['body'], batch['batchId'])
        author = comment['user']
        if command is None or author['type'] != 'User':
            continue
        issue = issues[number]
        login = author['login']
        owners = {a['login'].lower() for a in issue['assignees']}
        if command == 'claim':
            if issue['state'] != 'open' or owners:
                actions.append({'commentId': comment['id'], 'action': 'claim_ignored', 'reason': 'closed_or_assigned'})
                continue
            if dry_run:
                issue['assignees'] = [author]
            else:
                assigned = api.request(f'issues/{number}/assignees', 'POST', {'assignees': [login]})
                if login.lower() not in {a['login'].lower() for a in assigned['assignees']}:
                    raise RuntimeError('GitHub did not assign the commenter')
                issues[number] = assigned
            actions.append({'commentId': comment['id'], 'action': 'claimed', 'batchId': batch['batchId'], 'login': login})
        elif login.lower() in owners:
            if dry_run:
                issue['assignees'] = [a for a in issue['assignees'] if a['login'].lower() != login.lower()]
            else:
                issues[number] = api.request(f'issues/{number}/assignees', 'DELETE', {'assignees': [login]})
                if login.lower() in {a['login'].lower() for a in issues[number]['assignees']}:
                    raise RuntimeError('GitHub did not remove the commenter')
            actions.append({'commentId': comment['id'], 'action': 'unclaimed', 'batchId': batch['batchId'], 'login': login})
    # Read the current body after assignment work, preserving intervening editorial edits.
    current = api.request(f'issues/{ENTRY_ISSUE}')
    current_state = read_state(current['body'])
    for number, cursor in current_state['lastCommentIds'].items():
        cursors[number] = max(cursors.get(number, 0), cursor)
    body = update_entry_body(current['body'], batches, issues, {'schemaVersion': 1, 'lastCommentIds': cursors})
    changed = body != current['body']
    result = {'dryRun': dry_run, 'entryTitle': current['title'], 'entryBodyChanged': changed,
              'batches': len(batches), 'actions': actions}
    if changed and not dry_run:
        saved = api.request(f'issues/{ENTRY_ISSUE}', 'PATCH', {'body': body})
        result['entryTitle'] = saved['title']
        result['entryUrl'] = saved['html_url']
    return result


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ledger', type=Path, default=Path(__file__).resolve().parents[1]/'docs/community/publication-ledger.json')
    parser.add_argument('--dry-run', action='store_true')
    parser.add_argument('--receipt', type=Path)
    args = parser.parse_args()
    ledger = json.loads(args.ledger.read_text(encoding='utf-8'))
    if ledger['repository'] != REPOSITORY or os.environ.get('GITHUB_REPOSITORY', REPOSITORY) != REPOSITORY:
        raise ValueError('Unsupported repository')
    batches = ledger['batches']
    if len({b['issueNumber'] for b in batches}) != len(batches):
        raise ValueError('Duplicate batch issue number')
    if os.environ.get('GITHUB_EVENT_NAME') in {'issue_comment', 'issues'}:
        event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text(encoding='utf-8'))
        if event['issue'].get('pull_request') or event['issue']['number'] not in {b['issueNumber'] for b in batches}:
            print(json.dumps({'action': 'ignored', 'reason': 'issue_not_registered'}))
            return
    result = synchronize(GitHub(), batches, args.dry_run)
    if args.receipt:
        args.receipt.write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False))


if __name__ == '__main__':
    main()
