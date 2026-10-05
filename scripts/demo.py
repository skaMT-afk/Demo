"""Live, step-by-step EK1 demo. All responses come from the running API."""
import argparse
import json
import secrets
import sys
import time
from smoke import BASE, request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--auto', action='store_true', help='Run without Enter pauses (CI/rehearsal)')
    args = parser.parse_args()
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')

    def call(expected, method, path, data=None, token=None, show=True):
        if show:
            print(f'\n{method} {BASE}{path}')
            if token:
                print('Authorization: Bearer <скрыт>')
            if data is not None:
                print(json.dumps(data, ensure_ascii=False, indent=2))
        status, result = request(method, path, data, token)
        if show:
            print(f'HTTP {status}')
            print(json.dumps(result, ensure_ascii=False, indent=2))
        if status != expected:
            raise RuntimeError(f'{method} {path}: ожидался HTTP {expected}, получен {status}')
        return result

    def step(title):
        print('\n' + '=' * 60 + '\n' + title)
        if not args.auto:
            input('Нажми Enter, чтобы выполнить настоящий запрос... ')

    print('TaskFlow: живая демонстрация API. Адрес: ' + BASE)
    for _ in range(60):
        try:
            if request('GET', '/health')[0] == 200:
                break
        except (OSError, TimeoutError):
            pass
        time.sleep(1)
    else:
        raise RuntimeError('API не готов. Проверь docker compose ps и docker compose logs api.')
    step('1. Проверка API и подключения к PostgreSQL')
    call(200, 'GET', '/health')

    print('\nПодготовка: создаём двух тестовых пользователей и команду через API.')
    suffix = secrets.token_hex(4)
    password = secrets.token_hex(16)
    users, tokens = {}, {}
    for role in ('author', 'worker'):
        credentials = {'login': role + '_' + suffix, 'password': password}
        users[role] = call(201, 'POST', '/api/auth/register', credentials, show=False)['id']
        tokens[role] = call(200, 'POST', '/api/auth/login', credentials, show=False)['accessToken']
    team = call(201, 'POST', '/api/teams', {'name': 'Demo ' + suffix}, tokens['author'], False)['id']
    call(204, 'POST', f'/api/teams/{team}/members', {'userId': users['worker']}, tokens['author'], False)
    print('Автор, исполнитель и команда созданы. Пароли и токены не выводятся.')

    step('2. Автор создаёт задачу — Create')
    task = call(201, 'POST', f'/api/teams/{team}/tasks', {
        'assigneeId': users['worker'], 'title': 'Подготовить отчёт',
        'description': 'Собрать результаты работы команды.'}, tokens['author'])
    path = '/api/tasks/' + task['id']
    assert task['state'] == 'assigned' and task['version'] == 1
    step('3. Исполнитель читает сохранённую задачу — Read')
    assert call(200, 'GET', path, token=tokens['worker']) == task
    step('4. Исполнитель отправляет результат — изменение состояния')
    task = call(200, 'POST', path + '/submit', {'result': 'Отчёт подготовлен.', 'version': task['version']}, tokens['worker'])
    assert task['state'] == 'review' and task['version'] == 2
    step('5. Попытка исполнителя принять собственную работу — отказ 403')
    call(403, 'POST', path + '/accept', {'version': task['version']}, tokens['worker'])
    assert call(200, 'GET', path, token=tokens['author'], show=False) == task
    step('6. Автор принимает результат')
    task = call(200, 'POST', path + '/accept', {'version': task['version']}, tokens['author'])
    assert task['state'] == 'accepted' and task['version'] == 3
    assert call(200, 'GET', path, token=tokens['author'], show=False) == task
    for token in tokens.values():
        call(204, 'POST', '/api/auth/logout', token=token, show=False)
    print('\nDEMO PASS: задача создана, прочитана и принята; запрет самоприёмки подтверждён.')
    print('Данные сохранены в учебной БД. Каждый запуск создаёт новую команду и задачу.')


if __name__ == '__main__':
    try:
        main()
    except (OSError, TimeoutError, RuntimeError, AssertionError) as error:
        print(f'\nДемонстрация не завершена: {error}', file=sys.stderr)
        print('При HTTP 429 дождись следующей минуты. При недоступности API проверь Docker.', file=sys.stderr)
        sys.exit(1)
