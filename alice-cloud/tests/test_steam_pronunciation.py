"""Cross-script search and confirmation tests; no network or actual PC commands."""
import unittest
from unittest.mock import patch
import os
import test_steam
import steam_cloud as steam


class SearchTests(unittest.TestCase):
    def setUp(self):
        env = patch.dict(os.environ, {}, clear=True)
        env.start()
        self.addCleanup(env.stop)

    def test_multiple_titles_without_configured_aliases(self):
        titles = [('Dreadmoor', ['дредмо', 'дредмор', 'дредмур']),
                  ('Skyrim', ['скайрим']), ('Cyberpunk 2077', ['киберпанк 2077']),
                  ('Half-Life', ['халф лайф']), ('Stardew Valley', ['стар дью вэлли']),
                  ('Baldur\'s Gate 3', ['балдурс гейт 3'])]
        games = [{'appid': str(i + 1), 'name': name} for i, (name, _) in enumerate(titles)]
        for i, (_, variants) in enumerate(titles):
            for variant in variants:
                with self.subTest(query=variant):
                    found, exact = steam.matches(variant, games)
                    self.assertTrue(found)
                    self.assertEqual(found[0]['appid'], str(i + 1))
                    self.assertFalse(exact)

    def test_command_prefix_and_empty(self):
        games = [{'appid': '1', 'name': 'Skyrim'}]
        self.assertEqual(steam.game_query('Пожалуйста, запусти игру Скайрим!'), 'скайрим')
        self.assertEqual(steam.matches('пожалуйста запусти игру скайрим', games)[0], games)
        self.assertEqual(steam.matches('', games), ([], False))
        self.assertEqual(steam.matches('запусти игру', games), ([], False))
        self.assertEqual(steam.game_query('запусти игру'), '')

    def test_numbered_sequels_and_roman_numerals(self):
        games = [{'appid': '1', 'name': 'Dark Souls II'},
                 {'appid': '2', 'name': 'Dark Souls III'}]
        found, exact = steam.matches('дарк соулс 3', games)
        self.assertEqual(found, [games[1]])
        self.assertFalse(exact)
        self.assertIn('2077', steam.pronunciation('Cyberpunk 2077'))

    def test_unknown_short_and_semantic_translation(self):
        games = [{'appid': '1', 'name': 'Skyrim'}]
        for query in ('ы', 'абракадабра', 'неведомая игра', 'древние свитки'):
            self.assertEqual(steam.matches(query, games), ([], False))

    def test_existing_and_colliding_aliases(self):
        games = [{'appid': '1', 'name': 'Skyrim'}, {'appid': '2', 'name': 'Doom'}]
        with patch.dict(os.environ, {'STEAM_ALIASES': '{"1":["драконы"]}'}):
            self.assertEqual(steam.matches('драконы', games), ([games[0]], True))
        found, exact = steam.matches('моя игра', games, {'1': ['моя игра'], '2': ['моя игра']})
        self.assertEqual(found, games)
        self.assertTrue(exact)


class DialogTests(unittest.TestCase):
    setUp = test_steam.Tests.setUp
    event = test_steam.Tests.event
    call = test_steam.Tests.call

    def test_spoken_match_requires_confirmation_then_is_remembered(self):
        self.store.data['catalog.json']['games'] = [{'appid': '1', 'name': 'Dreadmoor'}]
        result = self.call('дредмо')
        self.assertEqual(result['session_state']['confirm'], '1')
        self.wake.assert_not_called()
        self.assertIsNone(self.store.get('spoken-aliases.json'))
        self.call('да', result['session_state'])
        self.assertEqual(self.store.get('spoken-aliases.json'), {'1': ['дредмо']})
        # A separate function/dialog instance reads durable learned aliases.
        self.wake.reset_mock()
        event = self.event('дредмо')
        event['session']['message_id'] = 3
        steam.dialog(event, self.cfg, self.store, self.auth, self.wake)
        self.wake.assert_called_once()

    def test_rejection_and_cancel_do_not_learn_or_launch(self):
        self.store.data['catalog.json']['games'] = [{'appid': '1', 'name': 'Dreadmoor'}]
        result = self.call('дредмо')
        rejected = self.call('нет', result['session_state'])
        self.assertEqual(rejected['session_state'], {})
        self.call('стоп', result['session_state'])
        self.wake.assert_not_called()
        self.assertIsNone(self.store.get('spoken-aliases.json'))

    def test_ambiguous_spoken_title_requires_number_and_learns_choice(self):
        result = self.call('дум')
        self.assertEqual(set(result['session_state']['choices']), {'10', '20'})
        self.wake.assert_not_called()
        self.call('2', result['session_state'])
        self.assertEqual(self.store.get('spoken-aliases.json'), {'20': ['дум']})

    def test_new_session_keeps_game_in_opening_command(self):
        event = self.event('запусти игру Doom')
        event['session']['new'] = True
        steam.dialog(event, self.cfg, self.store, self.auth, self.wake)
        self.wake.assert_called_once()

    def test_failed_search_does_not_assert_game_is_uninstalled(self):
        result = self.call('абракадабра')
        self.assertIn('Не удалось сопоставить', result['response']['text'])
        self.wake.assert_not_called()

    def test_learning_limits_and_obsolete_game(self):
        aliases = {'1': ['вариант ' + str(i) for i in range(8)]}
        steam.remember_alias(self.store, aliases, '1', 'новый вариант')
        self.assertEqual(len(aliases['1']), 8)
        self.assertEqual(aliases['1'][-1], 'новый вариант')
        steam.remember_alias(self.store, aliases, '2', 'а' * 129)
        self.assertNotIn('2', aliases)
        aliases = {str(i): ['игра'] for i in range(128)}
        steam.remember_alias(self.store, aliases, '999', 'другая игра')
        self.assertNotIn('999', aliases)
        self.call('да', {'confirm': '999', 'confirm_query': 'дредмо'})
        self.wake.assert_not_called()


if __name__ == '__main__':
    unittest.main()
