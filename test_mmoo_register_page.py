import sys
import io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

from selenium import webdriver
from bs4 import BeautifulSoup
import time

options = webdriver.ChromeOptions()
options.add_argument('--headless')
options.add_argument('--disable-gpu')
options.add_argument('--window-size=1280,1024')
driver = webdriver.Chrome(options=options)
driver.get('https://m.mmoo.team/Account/Register')
time.sleep(5)

soup = BeautifulSoup(driver.page_source, 'html.parser')
inputs = soup.find_all('input')
for i in inputs:
    print('input:', i.attrs)

driver.quit()
