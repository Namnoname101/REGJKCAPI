import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
from bs4 import BeautifulSoup
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/?r=QWP70O&app=1')
time.sleep(10)

soup = BeautifulSoup(driver.page_source, 'html.parser')
inputs = soup.find_all('input')
for i in inputs:
    print('input:', i.attrs)

buttons = soup.find_all(['button', 'a'])
for b in buttons:
    if 'register' in b.get('href', '').lower() or 'register' in b.get('class', [''])[0].lower() or 'đăng ký' in b.text.lower():
        print('btn/a:', b.name, b.attrs, b.text.strip()[:20])

driver.quit()
