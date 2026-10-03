"""End-to-end checks against an isolated, locally running EK1 API.
Creates uniquely named users/teams/tasks; never uses real credentials.
No third-party Python dependencies. A successful exit is required for PASS.
"""
import concurrent.futures
import json
import os
import secrets
import time
import urllib.error
import urllib.request

BASE = os.environ.get('API_URL', 'http://127.0.0.1:8080').rstrip('/')
count = 0

def request(method, path, data=None, token=None):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    body = None if data is None else json.dumps(data).encode()
    req = urllib.request.Request(BASE + path, body, headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=15) as response:
            status, raw = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, raw = error.code, error.read()
    try:
        result = json.loads(raw) if raw else None
    except json.JSONDecodeError:
        result = raw.decode(errors='replace')
    return status, result

def expect(status, method, path, data=None, token=None):
    global count
    actual, result = request(method, path, data, token)
    if actual != status:
        # Do not print response bodies: login responses can contain a token.
        raise AssertionError(f'{method} {path}: expected {status}, received {actual}')
    count += 1
    return result

def main():
    for attempt in range(60):
        try:
            if request('GET', '/health')[0] == 200:
                break
        except (OSError, TimeoutError):
            pass
        time.sleep(1)
    else:
        raise RuntimeError('API/DB did not become healthy within 60 attempts')
    expect(401, 'GET', '/api/tasks')
    expect(401, 'GET', '/api/tasks', token='0' * 64)
    users, tokens = {}, {}
    suffix = secrets.token_hex(4)
    password = secrets.token_hex(16)
    for role in 'ABCX':
        credentials = {'login': role.lower() + '_' + suffix, 'password': password}
        users[role] = expect(201, 'POST', '/api/auth/register', credentials)['id']
        tokens[role] = expect(200, 'POST', '/api/auth/login', credentials)['accessToken']
    expect(401, 'POST', '/api/auth/login', {'login': 'a_' + suffix, 'password': 'wrong-password'})
    team = expect(201, 'POST', '/api/teams', {'name': 'EK1 test ' + suffix}, tokens['A'])['id']
    team_url = '/api/teams/' + team
    for role in 'BC':
        expect(204, 'POST', team_url + '/members', {'userId': users[role]}, tokens['A'])
    expect(409, 'POST', team_url + '/members', {'userId': users['B']}, tokens['A'])
    expect(404, 'GET', team_url, token=tokens['X'])
    expect(404, 'POST', team_url + '/members', {'userId': users['X']}, tokens['X'])
    expect(403, 'POST', team_url + '/members', {'userId': users['X']}, tokens['B'])
    task_input = {'assigneeId': users['B'], 'title': 'Review task', 'description': 'Original'}
    expect(404, 'POST', team_url + '/tasks', task_input, tokens['X'])
    expect(400, 'POST', team_url + '/tasks', {**task_input, 'assigneeId': users['X']}, tokens['A'])
    expect(400, 'POST', team_url + '/tasks', {**task_input, 'assigneeId': users['A']}, tokens['A'])
    expect(400, 'POST', team_url + '/tasks', {**task_input, 'authorId': users['B']}, tokens['A'])
    expect(400, 'POST', team_url + '/tasks', {**task_input, 'state': 'accepted'}, tokens['A'])
    expect(400, 'POST', team_url + '/tasks', {**task_input, 'title': 'x' * 201}, tokens['A'])
    task = expect(201, 'POST', team_url + '/tasks', task_input, tokens['A'])
    assert task['authorId'] == users['A'] and task['state'] == 'assigned' and task['version'] == 1
    url = '/api/tasks/' + task['id']
    for role in 'AB':
        expect(200, 'GET', url, token=tokens[role])
        assert any(x['id'] == task['id'] for x in expect(200, 'GET', '/api/tasks', token=tokens[role]))
    for role in 'CX':
        expect(404, 'GET', url, token=tokens[role])
        assert not any(x['id'] == task['id'] for x in expect(200, 'GET', '/api/tasks', token=tokens[role]))
        expect(404, 'POST', url + '/submit', {'result': 'attack', 'version': 1}, tokens[role])
    expect(409, 'POST', url + '/accept', {'version': 1}, tokens['A'])
    expect(403, 'POST', url + '/submit', {'result': 'wrong role', 'version': 1}, tokens['A'])
    expect(400, 'POST', url + '/submit', {'result': ' ', 'version': 1}, tokens['B'])
    unchanged = expect(200, 'GET', url, token=tokens['A'])
    assert unchanged == task, 'Rejected requests mutated the task'
    literal = "R1: '); DROP TABLE Accounts; -- <script>alert(1)</script>"
    task = expect(200, 'POST', url + '/submit', {'result': literal, 'version': 1}, tokens['B'])
    assert task['state'] == 'review' and task['version'] == 2 and task['result'] == literal
    expect(409, 'POST', url + '/submit', {'result': 'overwrite', 'version': 2}, tokens['B'])
    for action in ('accept', 'return'):
        expect(403, 'POST', url + '/' + action, {'version': 2}, tokens['B'])
        expect(404, 'POST', url + '/' + action, {'version': 2}, tokens['C'])
    assert expect(200, 'GET', url, token=tokens['A']) == task
    task = expect(200, 'POST', url + '/return', {'version': 2}, tokens['A'])
    assert task['state'] == 'rework' and task['version'] == 3
    task = expect(200, 'POST', url + '/submit', {'result': 'R2', 'version': 3}, tokens['B'])
    assert task['state'] == 'review' and task['version'] == 4
    expect(409, 'POST', url + '/accept', {'version': 2}, tokens['A'])
    assert expect(200, 'GET', url, token=tokens['A']) == task
    # Both HTTP requests carry the same reviewed version. PostgreSQL must accept only one.
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        jobs = [pool.submit(request, 'POST', url + '/' + action, {'version': 4}, tokens['A'])
                for action in ('accept', 'return')]
        results = [job.result() for job in jobs]
    assert sorted(status for status, _ in results) == [200, 409], 'Concurrent writes were not isolated'
    task = expect(200, 'GET', url, token=tokens['A'])
    assert task['version'] == 5 and task['state'] in ('accepted', 'rework') and task['result'] == 'R2'
    if task['state'] == 'rework':
        task = expect(200, 'POST', url + '/submit', {'result': 'R3', 'version': 5}, tokens['B'])
        task = expect(200, 'POST', url + '/accept', {'version': task['version']}, tokens['A'])
    expect(409, 'POST', url + '/submit', {'result': 'late', 'version': task['version']}, tokens['B'])
    expect(409, 'POST', url + '/return', {'version': task['version']}, tokens['A'])
    assert expect(200, 'GET', url, token=tokens['A']) == task
    expect(200, 'GET', '/api/me', token=tokens['A'])  # SQL literal did not remove accounts.
    expect(204, 'POST', '/api/auth/logout', token=tokens['X'])
    expect(401, 'GET', '/api/tasks', token=tokens['X'])
    print(f'PASS: {count} HTTP status checks plus state, data isolation and concurrency assertions.')
    print('Scope: smoke checks only; expiry, storage inspection, TLS, full DoS and vulnerability audit are not covered.')

if __name__ == '__main__':
    main()
